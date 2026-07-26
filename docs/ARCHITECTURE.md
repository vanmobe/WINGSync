# Architectuur

## Doel en invarianten

WingSync synchroniseert alleen expliciet geselecteerde globale WING-secties
en kanaalmappings van één gezaghebbende bronconsole naar één doelconsole. De
standaardrichting is FOH → podium. De omgekeerde richting is mogelijk, maar
bidirectionele synchronisatie wordt door de validator geweigerd.

De veiligheidsinvarianten zijn:

1. droogloop is de standaard en verstuurt geen parameterwrites;
2. live vereist twee zichtbare, vastgezette en opnieuw geverifieerde
   hardware-serienummers;
3. bron en doel worden voor iedere eerste of herstelde reconciliatie vers
   ingelezen;
4. alleen de doorsnede van geselecteerde scope, geldige mapping en toegestane
   risklasse kan worden uitgevoerd;
5. iedere live write vereist readback; een mismatch pauzeert fail-closed;
6. configuratie- en transportgeneraties worden vlak voor dispatch opnieuw
   gecontroleerd;
7. cachedata is nooit invoer voor een writeplan en wordt nooit gereplayd.

## Componenten

```text
WPF UI
  │  setup, status, diffbevestiging, activiteit, herstel
  ▼
SyncCoordinator ── WritePlanner / ScopeCatalog / ConfigValidator
  │                     │
  │                     └─ mapping, risklasse, model- en sidechain-guards
  ├─ FOH IWingSession ── WingSync.WapiHost.exe A ── TCP/2222 ── WING FOH
  ├─ MON IWingSession ── WingSync.WapiHost.exe B ── TCP/2222 ── WING podium
  ├─ WingStateCacheSink ── atomische lokale cache
  └─ SyncDiagnosticsHub ── begrensde JSONL-audittrail
```

- `WingSync.Core` bevat domeinmodellen, validatie, token-/scopematching,
  writeplanning en de stateful coordinator.
- `WingSync.Infrastructure` bevat discovery, helperprocestransport, simulator,
  configuratie/cache, JSONL-diagnostiek en supportexport.
- `WingSync.App` is de Windows WPF-interface.
- `WingSync.WapiHost.exe` is een statisch gebouwde x64 MSVC-helper rond de
  officiële WAPI C-API.

## Waarom één helperproces per console

De WAPI C-API gebruikt procesglobale verbindingsstatus en levert geen
onafhankelijke sessiehandle. Twee verbindingen in één proces zouden elkaars
globale toestand kunnen beïnvloeden. WingSync start daarom één helperproces
per fysieke console en spreekt daarmee via een streng stdin/stdout-protocol.

Een nieuwe connectie vervangt altijd de vorige helpergeneratie. Stdout- en
stderrlezers van een gestopt proces worden afgewacht voordat een vervanger
actief wordt, zodat oude output niet in een nieuwe sessie kan terechtkomen.
Timeout, protocolfout, helpercrash of onverwachte disconnect markeert de
sessie als faulted en triggert de fail-closed reconnectstroom.

## Discovery en identity

Discovery verstuurt exact `WING?` via UDP/2222 op actieve IPv4-adapters en
dedupliceert antwoorden. Een `DiscoveredWing` bevat IP, naam, model,
serienummer, firmware en observatietijd. Native WAPI gebruikt vast TCP-poort
2222; een andere geconfigureerde poort is een validatiefout.

Live connecties gebruiken een `WingEndpoint` met een verwacht serienummer. De
identityverifier controleert IP én serial:

- voor de verbindingstest;
- voor de eerste live snapshot;
- na iedere reconnect;
- voor bron en doel afzonderlijk.

Dezelfde fysieke console mag niet beide rollen vervullen. Discovery of
cached metadata alleen verleent nooit writetoestemming.

## Reconciliatie en concurrency

De eerste en iedere herstelde synchronisatie verloopt als volgt:

1. blokkeer eventintake en live dispatch;
2. verbind beide geïsoleerde helpers en verifieer beide identities;
3. lees alle vereiste bron- en doelnodes vers in;
4. leg source-, target- en transportgeneratie bij de snapshot vast;
5. filter, map en vergelijk waarden tot een deterministische diff;
6. toon de diff in droogloop of wacht op expliciete livebevestiging;
7. controleer de generaties opnieuw bij batchstart en onmiddellijk voor
   `SetMany`;
8. voer alleen toegestane writes uit en lees ze terug;
9. geef eventintake vrij en verwerk gebeurtenissen die tijdens de snapshot
   veilig werden gebufferd.

Een `reconcileGate` serialiseert initialisatie, bevestiging, reconnect en
workerbatches. Daardoor kan een reconnect geen actieve bevestiging of
writebatch overlappen. Een bronwijziging die een preview invalideert levert
een nieuwe preview; ze mag niet met een oude bevestiging worden uitgevoerd.
Doeldrift wordt tegen een verse scalar van de gezaghebbende bron herpland.

De eventqueue is begrensd. Gebeurtenissen worden per token kort samengevoegd,
waarbij echte bronevents voorrang houden op synthetische doeldrift. Een
fingerprintvenster onderdrukt echoes van lokaal geschreven doelwaarden. Bij
queue-overflow, readbackmismatch of veiligheidsinconsistentie wordt live
ontwapend en intake gepauzeerd in plaats van gegevens te verliezen.

## Scopemodel

De UI volgt de globale WING Library-secties:

| UI-scope | Interne `SyncScope` | Canonieke tokenfamilie |
|---|---|---|
| `CUST` | `Cust` | naam, kleur, icoon, led |
| `TAGS` | `Tags` | tags |
| `CONN` | `Conn` | `in/conn`, inputselectie |
| `IN` | `In` | channel trim, balance, fase |
| `FILTER` | `Filter` | `flt` |
| `DELAY` | `Delay` | inputdelay |
| `GATE` | `Gate` | `gate`, `gatesc` |
| `DYN` | `Dyn` | `dyn`, `dynxo`, `dynsc` |
| `PRE` | `Pre` | `preins` |
| `POST` | `Post` | `postins` |
| `EQ` | `Eq` | `eq`, `peq` |
| `PAN` | `Pan` | pan en width |
| `MAIN` | `Main1`–`Main4` | `main/1`–`main/4` |
| `BUS` | `Send` | `send` |
| `FADER` | `Fdr` | `fdr` |
| `MUTE` | `Mute` | `mute` |
| `CONFIG` | `Config` | procesvolgorde, tap, solo-safe, monitor |

Hierdoor blijven de operatorlabels `MAIN`, `BUS` en `FADER` gelijk aan de
globale WING-UI, terwijl de interne enum de WAPI-tokenfamilies nauwkeurig
weergeeft.

De veilige standaardselectie is `CUST`, `FILTER`, `DELAY`, `GATE`, `DYN` en
`EQ`. `TAGS`, `CONN`, `PRE`, `POST`, `MAIN`, `BUS`, `FADER`, `MUTE` en
`CONFIG` hebben verhoogd risico en vereisen een aparte toestemming. `IN` en
`PAN` zijn niet als verhoogd risico geclassificeerd, maar staan conservatief
standaard uit.

Alleen channelpaths voor CH 1–40 en AUX 1–8 worden herschreven. Read-only
`$`-tokens en onbekende paden vallen buiten het plan. Fysieke head-ampgain,
phantom power en `/io/in`-routing vallen expliciet buiten `IN`. Embedded
gate-/dynamics-sidechainreferenties moeten eenduidig kunnen worden gemapt;
anders wordt de volledige betrokken processorgroep geblokkeerd. Voor
modelwissels plant de engine veilige fasen: processor uit, model, parameters
en als laatste de gewenste aan/uitstand.

## Droogloop en livebevestiging

Droogloop gebruikt dezelfde verse snapshots, planner en safetyguards als
live, maar markeert alle uitvoerbare verschillen als preview en roept geen
WAPI-set aan. Metrics onderscheiden previews, geverifieerde writes en
geblokkeerde verschillen.

Live start in `AwaitingConfirmation`. De operator ziet bron, doel, serials,
richting, scopes, mappingaantal, totaaldiff, uitvoerbare en geblokkeerde
aantallen en verdeling per scope. De standaardactie is weigeren. Wanneer een
verhoogd-risicoscope actief is, volgt een tweede onafhankelijke bevestiging.
Wijziging van identity, IP, richting, scope of mapping trekt live/high-risk
toestemming in, zet de configuratie terug op droogloop en maakt de
verbindingstest ongeldig.

## Cache en opslag

Configuratie, cache, supportbestanden en JSONL-logs staan standaard onder
`%LOCALAPPDATA%\WingSync`; tests en support kunnen een expliciete datamap
gebruiken.

Configuratie en cache worden met een tijdelijk bestand, flush en atomische
vervanging opgeslagen. Een cachepartition bevat onder meer console-serial,
model, firmware, parameterpad, type, waarde, observatietijd en connection
epoch. Writes naar de cache worden geserialiseerd met reset en dispose.
Beschadigde, incompatibele of identity-mismatched bestanden worden
geïsoleerd; een geldige backup kan voor offline weergave worden hersteld.

Er is bewust geen pad van cache naar planner of WAPI-dispatch. De UI kan
zonder consoleverbinding het aantal waarden en de leeftijd van een bekende
snapshot tonen, maar markeert dit als offline/stale. Een reconnect begint
altijd met identityverificatie en verse consoledata.

## Diagnostiek en privacy

De diagnostiek gebruikt begrensde, roterende JSONL-logs. Een probleem met
loggen mag de realtime engine niet blokkeren; dropped entries blijven als
teller zichtbaar.

Een supportpakket wordt naar een tijdelijk zipbestand geschreven en daarna
atomisch geplaatst. Het bevat een gesaniteerde configuratiestructuur,
gesaniteerde logs en een korte statusdiagnose. De export redigeert:

- IPv4- en IPv6-adressen;
- WING-serienummers;
- lokale gebruikersprofielpaden;
- parameter- en readbackwaarden;
- ruwe helper- of verificatieberichten die waarden kunnen bevatten.

Cachebestanden en consolesnapshots worden niet opgenomen. De export vraagt
vooraf toestemming en overschrijft geen bestaand pakket.

## Build- en distributiegrens

De managed code target .NET 10 voor Windows en bouwt met nullable,
latest-recommended analyzers en warnings-as-errors. De release publiceert de
WPF-app self-contained en single-file voor win-x64. De native WAPI-helper
blijft als afzonderlijk bestand naast `WingSync.exe`, omdat die procesgrens
onderdeel is van het sessiemodel. De draagbare zip bevat ook de
gebruikershandleiding en WAPI-licentiebestanden.
