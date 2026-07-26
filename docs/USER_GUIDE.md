# WingSync gebruikershandleiding

WingSync volgt een live-audiowerkwijze: eerst identiteit en richting
controleren, daarna een droogloop beoordelen en pas daarna bewust live
inschakelen. De standaardkeuze in een livebevestiging is altijd **Nee**.

> **Huidige vrijgavegrens:** identity, read-only en begrensde fysieke
> write/readback/herstelproeven zijn geslaagd. Fysieke kabelonderbreking,
> eventstormmeting en duurtest zijn nog niet afgerond. De portable build is
> bovendien niet Authenticode-ondertekend. Gebruik live synchronisatie daarom
> nog niet tijdens een show; droogloop en demo zijn wel geschikt voor
> voorbereiding en beoordeling.

## Voorbereiding

1. Maak op beide WINGs een actuele showbackup.
2. Verbind de Windows-pc en de controlpoorten van beide consoles met hetzelfde
   geïsoleerde IPv4-control-netwerk.
3. Laat UDP en TCP poort 2222 toe voor WingSync en
   `WingSync.WapiHost.exe`. Een afwijkende WAPI-poort wordt niet ondersteund.
4. Start `WingSync.exe`. De app start met **Droogloop (geen writes)** actief.
5. Controleer in de bovenbalk altijd richting, sessiestatus en modus voordat
   je een actie uitvoert.

De finale automatisering is uitgevoerd op Windows x64 build 26200. De app is
self-contained; oudere Windows-builds zijn niet in deze acceptatieronde
gevalideerd.

Gebruik voor training zonder hardware:

```powershell
.\scripts\run.ps1 -Demo
```

## Eenvoudige setup in vier stappen

De pagina **Status** toont een checklist en steeds de eerstvolgende veilige
actie.

### 1. Consoles vinden en identiteit vastzetten

Open **Synchronisatie** en kies **Consoles zoeken**. Wijs één fysieke console
aan FOH en de andere aan podium toe. Controleer in de keuzelijst en op de twee
statuskaarten:

- consolenaam en rol;
- IP-adres;
- model en firmware;
- volledig serienummer;
- de labels **BRON** en **DOEL**.

Een zichtbaar serienummer is een hardware-pin, niet alleen beschrijvende
informatie. Livegebruik blijft geblokkeerd wanneer een serial ontbreekt,
wanneer hetzelfde serienummer voor beide rollen is gekozen of wanneer IP en
serial bij een latere controle niet meer samenhoren.

De aanbevolen richting is **FOH → Podium**. **Podium → FOH** is ondersteund,
maar geeft bewust een waarschuwing. Er is geen bidirectionele modus.

### 2. Scopes en kanaalmapping kiezen

Kies alleen de globale WING-secties die voor deze workflow nodig zijn:

| UI-scope | Inhoud | Veilige standaard |
|---|---|---:|
| `CUST` | naam, kleur, icoon en licht | aan |
| `TAGS` | tags en DCA-/mutetoewijzingen | uit, verhoogd risico |
| `CONN` | bron A/B en inputselectie | uit, verhoogd risico |
| `IN` | trim, balance en fase; geen fysieke preamp of phantom | uit |
| `FILTER` | HPF, LPF, tilt en all-pass | aan |
| `DELAY` | inputdelay | aan |
| `GATE` | gate, model en sidechain | aan |
| `DYN` | dynamics, model, crossover en sidechain | aan |
| `PRE` | pre-inserttoewijzing | uit, verhoogd risico |
| `POST` | post-inserttoewijzing | uit, verhoogd risico |
| `EQ` | channel- en pre-send-EQ | aan |
| `PAN` | pan en width | uit |
| `MAIN` | Main 1–4-toewijzing en levels | uit, verhoogd risico |
| `BUS` | bus- en matrixsends | uit, verhoogd risico |
| `FADER` | kanaalfader | uit, verhoogd risico |
| `MUTE` | kanaalmute | uit, verhoogd risico |
| `CONFIG` | procesvolgorde, tap, solo-safe en monitorconfiguratie | uit, verhoogd risico |

`MAIN`, `BUS` en `FADER` zijn de globale namen die de operator in de UI ziet.
Intern correspondeert `MAIN` met Main 1–4, `BUS` met WAPI-sectie `SEND` en
`FADER` met `FDR`.

Vul daarna de mapping in:

- inputkanalen hebben bereik 1–40;
- AUX-kanalen hebben bereik 1–8 en vormen een aparte reeks;
- ieder actief bronkanaal en ieder actief doelkanaal mag per reeks maar één
  keer voorkomen;
- **1 → 1 invullen** vraagt bij een aangepaste lijst eerst om bevestiging met
  standaardkeuze **Nee** en vervangt na **Ja** de lijst; controleer ze meteen;
- een niet-oplosbare sidechainreferentie blokkeert de volledige betrokken
  gate- of dynamics-processorgroep.

**Configuratie opslaan** schrijft de configuratie atomisch naar de lokale
datamap. Validatiefouten staan gezamenlijk onder de mapping; los ze allemaal
op voordat je start.

### 3. Beide verbindingen testen

Kies **Verbinding testen**. WingSync controleert voor beide rollen:

- de ontdekte IP/serial-combinatie;
- dat FOH en podium verschillende fysieke consoles zijn;
- een afzonderlijke WAPI-verbinding;
- keepalive/statusreadback.

De kaarten tonen per console wanneer voor het laatst echte data werd gelezen.
Een discoverytijd of een lokale cache geldt niet als een geslaagde
verbindingstest.

### 4. Droogloop uitvoeren en preview beoordelen

Laat **Droogloop (geen writes)** aan en kies **Start droogloop**. WingSync leest
beide consoles vers in, bouwt de mappingdiff en logt wat er zou gebeuren. Er
wordt in deze modus geen parameterwrite naar een console gestuurd.

Beoordeel op **Status** drie afzonderlijke tellers:

- **Preview / gepland**: verschillen die zonder consolewrite zijn berekend;
- **Geverifieerde live writes**: alleen werkelijk uitgevoerde writes waarvan
  de readback overeenkwam;
- **Geblokkeerd**: verschillen die door scope-, mapping- of safetyregels niet
  uitvoerbaar waren.

Gebruik **Activiteit** om de exacte tokens, scopes, reconnects en problemen te
controleren. Stop na de beoordeling. De checklist geeft daarna de veilige
overgang naar live aan.

## Van droogloop naar live

Voer deze procedure alleen in een onderhoudsvenster uit:

1. Controleer opnieuw de backups, richting, zichtbare serienummers, scopes en
   mapping.
2. Stop de droogloop.
3. Schakel **Droogloop (geen writes)** uit. Readback van iedere write is altijd
   actief en kan niet worden uitgezet.
4. Schakel **Scopes met verhoogd risico toestaan** alleen in wanneer de
   geselecteerde tags, routing, inserts, mains, sends, faders, mutes of
   configuratie ook werkelijk live mogen wijzigen.
5. Kies **Start live**. Tijdens verbinden, snapshots en de preview staat de
   modus op **WORDT VOORBEREID · GEEN WRITES**.
6. Controleer in de bevestiging bron- en doel-IP, beide serial-pins, richting,
   scopes, aantal mappings, uitvoerbare wijzigingen, geblokkeerde wijzigingen
   en de verdeling per scope. Kies alleen dan **Ja**.
7. Voor geselecteerde verhoogd-risicoscopes volgt een tweede, aparte
   bevestiging van de showbackups.
8. Na **Ja** toont WingSync tijdens de initiële toepassing
   **LIVE WORDT TOEGEPAST · WRITES + READBACK**. In die fase zijn bevestigde
   writes en hun verplichte readback actief. Pas na succesvolle afronding
   volgt **LIVE ACTIEF · READBACK OK**.

Wijzig je een console, IP, richting, scope of mapping, dan trekt WingSync de
live- en verhoogd-risicotoestemming in, schakelt het terug naar droogloop en
maakt de verbindingstest ongeldig. Herhaal vanaf de relevante setupstap.

Gebruik de rode **Stop**-knop of `Ctrl+Shift+S` als noodstop. Daarmee worden
nieuwe en nog niet verstuurde writes onmiddellijk geblokkeerd. Een WAPI-write
die al naar de console is verstuurd, mag alleen zijn begrensde
transactie/readback veilig afmaken voordat de sessies worden losgekoppeld.
Neem bij direct audiorisico daarnaast de normale fysieke audio-maatregelen;
wacht niet uitsluitend op een softwarestatus.

## Status en storingen

- **LIVE UIT · DROOGLOOP**: veilig voorbereid; er worden geen writes gedaan.
- **DROOGLOOP · GEEN WRITES**: snapshots en previews mogen lopen.
- **WORDT VOORBEREID · GEEN WRITES**: live is aangevraagd, maar nog niet
  bevestigd.
- **LIVE WORDT TOEGEPAST · WRITES + READBACK**: de bevestigde initiële diff of
  reconnect-catch-up wordt geschreven en iedere batch wordt teruggelezen.
- **LIVE ACTIEF · READBACK OK**: de live engine draait en uitgevoerde writes
  worden teruggelezen.
- **LIVE GEPAUZEERD · GEEN WRITES** of **GEBLOKKEERD · GEEN WRITES**: er is
  geen toestemming om verder te schrijven.

De kleuren ondersteunen die tekst:

- groen: aantoonbaar actieve en geverifieerde livewerking;
- blauw: droogloop;
- geel/oranje: gereedmaken, reconnect of waarschuwing;
- rood: blokkerende fout of stopactie;
- grijs: uit/offline.

Bij verlies van één console, een gewijzigde identity, queue-overflow,
helperfout of mislukte readback pauzeert WingSync de writes. Na reconnect
worden identity en beide toestanden opnieuw gelezen en wordt een nieuwe diff
gemaakt. Een foutbanner noemt de oorzaak en de herstelactie; hervat niet
voordat de oorzaak is opgelost en de nieuwe preview is beoordeeld.

## Lokale cache

WingSync bewaart de laatst waargenomen toestand per console onder
`%LOCALAPPDATA%\WingSync\cache`. Op de statuskaarten staan per FOH en podium
het aantal lokale waarden, de leeftijd en of een backup werd hersteld.

De cache is uitsluitend voor offline weergave en diagnose:

- ze vervangt geen live identity- of verbindingstest;
- ze wordt niet als bron voor een synchronisatieplan gebruikt;
- ze wordt na reconnect nooit afgespeeld;
- een serial-, firmware- of schema-afwijking maakt de cache stale of laat ze
  isoleren;
- een beschadigd bestand wordt geïsoleerd in plaats van blind geladen.

Onder **Instellingen → Cache vernieuwen** kun je de cache na het stoppen
resetten. De vorige cache wordt in een gedateerde quarantainemap bewaard.

## Activiteit, logs en supportpakket

De pagina **Activiteit** kan filteren op alles, problemen, writes en netwerk.
**Wissen** verwijdert alleen de in-memory lijst; de lokale JSONL-logbestanden
blijven behouden.

**Supportpakket exporteren** vraagt eerst toestemming en maakt onder
`%LOCALAPPDATA%\WingSync\support` een zip met:

- een geredigeerde configuratiestructuur;
- geredigeerde JSONL-loggebeurtenissen;
- productversie, coordinatorstatus en dropped-logteller.

IP-adressen, serienummers, lokale gebruikerspaden en WING-parameterwaarden
worden altijd geredigeerd. Cachebestanden en consolesnapshots worden niet
opgenomen. Controleer ook een geredigeerd pakket voordat je het extern deelt.

## Sneltoetsen

- `Ctrl+1`: Status
- `Ctrl+2`: Synchronisatie
- `Ctrl+3`: Activiteit
- `Ctrl+4`: Instellingen
- `Ctrl+Shift+S`: synchronisatie onmiddellijk stoppen

De editor wordt tijdens een actieve sessie vergrendeld. Stop eerst voordat je
topologie of safetyinstellingen aanpast.
