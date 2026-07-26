# WingSync

WingSync is een zelfstandige Windows-app voor veilige, configureerbare
eenrichtingssynchronisatie tussen een Behringer WING aan FOH en een WING op
het podium. De operator kiest de bron, het doel, de kanaalmapping en de globale
WING-secties. De app begint in droogloop, toont de verse verschilpreview en
vereist bij livegebruik readback van iedere uitgevoerde write.

> **Vrijgavestatus:** de geautomatiseerde core-, integratie- en UI-suites zijn
> groen. De fysieke identity-, read-only-, live write/readback- en
> herstelproeven op twee WINGs zijn eveneens geslaagd. Een fysieke
> kabelonderbreking, eventstormmeting en duurproef zijn nog **PENDING**.
> Het pakket is daarom een niet-ondertekende interne evaluatierelease; gebruik
> live synchronisatie nog niet tijdens een productie. Zie
> [het test- en acceptatierapport](docs/TEST_REPORT.md).

## Belangrijkste eigenschappen

- Ontdekking van twee WINGs met zichtbare naam, IP, model, firmware en
  serienummer; livegebruik vereist een serial-pin voor beide rollen.
- FOH → podium als aanbevolen richting en podium → FOH als bewuste
  eenrichtingsoptie. Bidirectionele synchronisatie is niet toegestaan.
- Mapping van inputkanalen 1–40 en AUX-kanalen 1–8, met controle op dubbele of
  ongeldige bron- en doelkanalen.
- Selectie volgens de globale WING-secties in de UI:
  `CUST`, `TAGS`, `CONN`, `IN`, `FILTER`, `DELAY`, `GATE`, `DYN`, `PRE`,
  `POST`, `EQ`, `PAN`, `MAIN`, `BUS`, `FADER`, `MUTE` en `CONFIG`.
  `MAIN` groepeert Main 1–4; `BUS` en `FADER` zijn de operatornamen voor de
  interne WAPI-families `SEND` en `FDR`.
- Een veilige droogloop → verse diff → expliciete livebevestiging. Routing,
  inserts, tags en mixbediening blijven daarnaast achter een aparte
  verhoogd-risicotoestemming.
- Begrensde eventverwerking, echo-onderdrukking, reconnect met nieuwe
  snapshots en een blokkerende pauze bij identity- of readbackproblemen.
- Een lokale cache per fysieke console voor offline statusweergave. Cachedata
  wordt nooit gebruikt om writes te plannen en wordt nooit naar een WING
  teruggespeeld.
- JSONL-diagnostiek en een supportpakket waarin IP-adressen, serienummers,
  lokale gebruikerspaden en WING-parameterwaarden worden geredigeerd.

## Gebruik

De draagbare release bevat `WingSync.exe` en de geïsoleerde
`WingSync.WapiHost.exe`; er hoeft geen .NET-runtime te worden geïnstalleerd.
Plaats beide bestanden bij elkaar en volg daarna de
[gebruikershandleiding](docs/USER_GUIDE.md). Voor discovery en WAPI moeten UDP
en TCP poort 2222 op het geïsoleerde control-netwerk bereikbaar zijn.

Voor een veilige demonstratie zonder consoles:

```powershell
.\scripts\run.ps1 -Demo
```

## Bouwen en testen

Voor ontwikkeling zijn Windows x64, .NET SDK 10.0.302, CMake en de x64
MSVC-toolchain van Visual Studio 2022 nodig. De officiële WAPI-bestanden en de
bijbehorende SLA staan in de repository.

```powershell
.\scripts\test.ps1 -Configuration Release
.\scripts\build.ps1 -Configuration Release
```

`test.ps1` bouwt standaard eerst en voert daarna 77 core-tests, 76
integratie-/foutinjectietests, de native helper-self-test en 14
UI-automatiseringstests uit. De UI-tests vereisen een ontgrendelde interactieve
Windows-desktopsessie.

`build.ps1` herhaalt de tests, publiceert de self-contained win-x64-app en
maakt:

- `artifacts\WingSync-win-x64-internal-evaluation\`
- `artifacts\WingSync-portable-win-x64-internal-evaluation.zip`
- `artifacts\WingSync-portable-win-x64-internal-evaluation.zip.sha256`

De build voert de volledige UI-suite ook uit tegen exact de gepubliceerde
single-file-app én opnieuw tegen de uit de zip uitgepakte app. Het buildmanifest
registreert bronhash, testbewijs en Authenticode-status. Zonder een vertrouwd
code-signingcertificaat blijft de status bewust
`internal-evaluation-unsigned`.

Gebruik `-SkipTests` of `-SkipBuild` alleen wanneer de relevante resultaten
voor exact dezelfde bron en configuratie al beschikbaar zijn.

## Documentatie

- [Gebruikershandleiding](docs/USER_GUIDE.md)
- [Architectuur en safetygrenzen](docs/ARCHITECTURE.md)
- [Test- en acceptatierapport](docs/TEST_REPORT.md)
- [WAPI-licentie-informatie](LICENSES/README.md)
