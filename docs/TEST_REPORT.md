# Test- en acceptatierapport

Laatste documentupdate: **2026-07-26**.

Een productiebuild voor live audio mag pas worden vrijgegeven wanneer alle
verplichte fysieke acceptatierijen **PASS** zijn en iedere tijdelijke
hardwarewijziging exact is teruggelezen én hersteld. Een groene
geautomatiseerde suite vervangt die consoleacceptatie niet.

## Geautomatiseerd bewijs

| Suite | Resultaat | Dekking / bewijs |
|---|---:|---|
| Core unit-tests | **PASS · 77/77** | domein, configuratievalidatie, scope-/tokenmatching, mappings, risklassen, plannerfasen en sidechainguards |
| Simulator- en foutinjectietests | **PASS · 76/76** | initial sync, bevestiging, exacte scalarcertificatie, processorguards, readback, noodstopraces, drift, echo, pollingbudget, queue-overflow, reconnectgeneraties, concurrency, cache-reset en supportredactie |
| UI-automatisering | **PASS · 14/14** | echte WPF/UI Automation-hoofdflows, toegankelijkheid, minimumformaat, persistence, offline cache, actieve-writestatus, live- en high-riskbevestiging en sneltoetsen |
| Native helper self-test | **PASS** | `WingSync.WapiHost.exe --self-test` |
| Release managed build | **PASS** | 0 warnings, 0 errors; warnings-as-errors en latest-recommended analyzers |

De drie managed testsuites zijn dependency-free uitvoerbare testharnassen. De
actuele volledige opdracht is:

```powershell
.\scripts\test.ps1 -Configuration Release
```

Die opdracht bouwt managed en native code, voert 77 core-tests en 76
integratietests uit, start de native self-test en sluit af met 14
UI-automatiseringstests. UI Automation vereist een ontgrendelde interactieve
Windows-desktopsessie; een headless of vergrendelde sessie is geen geldig
UI-testbewijs.

### Belangrijkste regressiedekking

- Live initial sync kan zonder actuele bevestiging niet schrijven.
- Een bron- of doelwijziging tijdens een preview invalideert die preview.
- Een transportgeneratiewijziging vlak voor `SetMany` forceert een nieuwe
  snapshot/bevestiging.
- Reconnect, actieve workerbatch en bevestiging worden geserialiseerd; dubbele
  reconnecttriggers veroorzaken geen dubbele write.
- Een noodstop tijdens een reeds verstuurde bevestigingsbatch laat alleen die
  batch en readback terminaal eindigen en blokkeert iedere volgende unit.
- Initiële live-uitvoering en reconnect-catch-up melden expliciet dat writes
  en readback actief zijn.
- Echte bronevents blijven gezaghebbend bij gelijktijdige doeldrift.
- Readbackmismatch, identitymismatch en queue-overflow pauzeren fail-closed.
- Trage periodieke reads mogen een reeds verstuurde WAPI-opdracht afmaken en
  veroorzaken geen onterechte helper-recycle of reconnectstorm.
- Model- en delaytransacties houden de processor veilig uit, herschrijven
  reset-side-effects en herstellen de gewenste enablewaarde pas na volledige
  exacte groepsreadback.
- Een blijvend conflict tussen een model-nodecache en de exacte modelscalar
  pauzeert zonder targetwrite.
- Een onoplosbare gate- of dynamics-sidechain blokkeert de hele betrokken
  processorgroep.
- Cache-reset is lineair met pending writes; dispose draineert reeds
  geaccepteerd werk.
- Een oude WAPI-helperreader kan niet in een nieuwe procesgeneratie
  publiceren.
- Het supportpakket redigeert configuratie, identity, adressen, lokale paden
  en parameterwaarden; malformed broninhoud wordt weggelaten.

### UI-scenario’s

De 14 geslaagde scenario’s zijn:

1. startdefaults en accessibilitycontract;
2. navigatie naar alle vier pagina’s;
3. mapping, AUX, ongeldige invoer en startvalidatie;
4. configuratiepersistentie na herstart;
5. droogloop start/stop/herstart en editor-lock;
6. veilige standaardkeuze **Nee** bij live-preview;
7. waarheidsgetrouwe actieve-writestatus en noodstop tijdens bevestigde live
   toepassing;
8. aparte high-riskreview en -bevestiging;
9. sluiten tijdens een actieve sessie;
10. compleet en leesbaar geredigeerd supportpakket;
11. bereikbare primaire acties op minimumvensterformaat;
12. zichtbare identities, waarheidsgetrouwe metrics en ingetrokken approvals
    na topologiewijziging;
13. offline cache na herstart zonder replay;
14. `Ctrl+1`–`Ctrl+4` en noodstop `Ctrl+Shift+S`.

## Fysieke WING-acceptatie

Op 2026-07-26 zijn de begrensde identity-, read-only- en
write/readback/herstelproeven opnieuw uitgevoerd op twee seriëel vastgezette
WING-varianten met firmware 3.1. De harness koos pas na een verse,
uitsluitend-lezen veiligheidsselectie een stil kanaal. Vier toegestane
stimuli zijn op bron en doel exact teruggelezen en daarna exact hersteld; het
write-through journal bevat één `recovery-complete`-marker.

| Fysieke proef | Status | Vereist bewijs |
|---|---:|---|
| Verse discovery en exacte identity-pin van beide consoles | **PASS** | verse pair-discovery; WAPI-serial na connect opnieuw exact aan iedere rol gebonden |
| Twee onafhankelijke WAPI-helperverbindingen | **PASS** | `$SYSCFG` 26, `$STAT` 32, vier keepalives in ≥12 s en clean disconnect |
| Read-only scoped snapshot | **PASS** | 286 CH40-items per console; geen writes |
| Live `CUST` write/readback/restore | **PASS** | alleen goedgekeurde stille-kanaaltoken; exact herstel |
| Live uitgeschakelde `EQ` scalar/readback/restore | **PASS** | kanaal aantoonbaar stil; processor uit; exact herstel |
| Live uitgeschakelde `GATE` scalar/readback/restore | **PASS** | kanaal aantoonbaar stil; processor uit; exact herstel |
| Live uitgeschakelde `DYN` scalar/readback/restore | **PASS** | kanaal aantoonbaar stil; processor uit; exact herstel |
| Fysieke kabelonderbreking en reconnect/resnapshot | **PENDING** | beide richtingen, geen stale replay of dubbele write |
| Fysieke latency-/eventstormmeting | **PENDING** | p95/p99 en convergentie met begrensd geheugen |
| Duurtest | **PENDING** | minimaal 8 uur, geen ongeplande writes, leaks of stille faults |

Een eerder op het netwerk waargenomen apparaat of een geslaagde ping is
hoogstens inventarisinformatie en geldt niet als een PASS in deze tabel.

## Veilige hardwareprocedure

De read-only proef kan expliciet worden gestart met:

```powershell
.\scripts\test.ps1 -Configuration Release `
    -LiveReadOnly `
    -FohSerial '<exacte-foh-serial>' `
    -StageSerial '<exacte-podium-serial>'
```

De live writeproef is opzettelijk dubbel vergrendeld:

```powershell
.\scripts\test.ps1 -Configuration Release `
    -LiveWrites `
    -AcknowledgeLiveWrites `
    -FohSerial '<exacte-foh-serial>' `
    -StageSerial '<exacte-podium-serial>'
```

Voer die opdracht alleen uit in een onderhoudsvenster, na showbackups en met
beide outputs fysiek veilig. De harness moet vóór iedere write bewijzen dat
CH40 op beide consoles stil, uitgeschakeld en ongerouteerd is. De allowlist
bestaat uitsluitend uit `CUST` en één scalar van een uitgeschakelde `EQ`,
`GATE` en `DYN`. Voor elke mutatie wordt eerst een write-through
recovery-journal opgeslagen; de `finally`-fase herstelt bron én doel exact en
leest het herstel terug.

De applicatie zelf gebruikt alleen WAPI via UDP/TCP 2222. De live
acceptatieharness gebruikt aanvullend UDP 2223 naar de FOH-console als
onafhankelijke, strikt geallowliste OSC-stimulus; dat pad zit niet in de
productieapp. Recovery-journals staan onder
`%LOCALAPPDATA%\WingSync\HardwareTestRecovery`.

Een test met een incomplete preflight, onverwachte identity, gewijzigde
originele waarde, niet-goedgekeurde token of mislukt herstel is **FAIL**, ook
wanneer de write zelf technisch lukte. Een recovery-journal blijft dan een
blokkerende actie voordat de consoles opnieuw mogen worden ingezet.

## Acceptatiegrenzen

- Geen enkele write buiten de geselecteerde richting, mapping, scopes en
  expliciete testallowlist.
- Iedere live write heeft overeenkomende readback of veroorzaakt onmiddellijk
  een zichtbaar, blokkerend probleem.
- p95 propagatie < 100 ms en p99 < 250 ms op het acceptatienetwerk.
- 1.000 events convergeren binnen 2 seconden met begrensd geheugen.
- Na netwerkherstel binnen 10 seconden opnieuw online, na exacte identitycheck
  en verse snapshots, zonder stale replay.
- Cachecorruptie leidt tot quarantine/rebuild en nooit tot een consolewrite of
  applicatiecrash.
- Na de duurtest zijn helperprocessen, handles, queuegebruik, logdrop en
  geheugengebruik stabiel.

## Vrijgavebeslissing

**NO-GO voor productie-livegebruik zolang de fysieke reconnect-, eventstorm-
en duurproeven PENDING zijn.** De huidige niet-ondertekende portable build is
een interne evaluatierelease. Droogloop, simulator en UI-workflow mogen wel
worden gebruikt om configuratie en bedieningsprocedures voor te bereiden.
