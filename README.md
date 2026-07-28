# WingSync

WingSync is a standalone Windows app for safe, configurable one-way
synchronization between a Behringer WING at FOH and a WING on stage. The
operator selects the source, the target, the channel mapping, and the global
WING sections. The app starts in dry run mode, shows the fresh diff preview,
and requires readback for every executed write during live use.

> **Release status:** the automated core, integration, and UI suites are
> green. The physical identity, read-only, live write/readback, and recovery
> tests on two WINGs have also passed. A physical cable interruption,
> event-storm measurement, and endurance test are still **PENDING**. The
> package is therefore an unsigned internal evaluation release; do not use
> live synchronization during a production yet. See the
> [test and acceptance report](docs/TEST_REPORT.md).

## Key features

- Discovery of two WINGs with visible name, IP, model, firmware, and serial
  number; live use requires a serial pin for both roles.
- FOH → stage as the recommended direction and stage → FOH as a deliberate
  one-way option. Bidirectional synchronization is not allowed.
- Mapping of input channels 1–40 and AUX channels 1–8, with validation for
  duplicate or invalid source and target channels.
- Selection by the global WING sections in the UI:
  `CUST`, `TAGS`, `CONN`, `IN`, `FILTER`, `DELAY`, `GATE`, `DYN`, `PRE`,
  `POST`, `EQ`, `PAN`, `MAIN`, `BUS`, `FADER`, `MUTE`, and `CONFIG`.
  `MAIN` groups Main 1–4; `BUS` and `FADER` are the operator-facing names for
  the internal WAPI families `SEND` and `FDR`.
- A safe flow of dry run → fresh diff → explicit live confirmation. Routing,
  inserts, tags, and mix control also remain behind a separate high-risk
  approval.
- A persistent Setup → Connection → Dry run → Review → Live timeline with
  immediate header actions in a dark operator theme.
- Bounded event processing, echo suppression, reconnect with fresh snapshots,
  and a blocking pause on identity or readback problems.
- A local cache per physical console for offline status display. Cache data is
  never used to plan writes and is never replayed to a WING.
- JSONL diagnostics and a support bundle that redacts IP addresses, serial
  numbers, local user paths, and WING parameter values.

## Usage

The portable release contains `WingSync.exe` and the isolated
`WingSync.WapiHost.exe`; no .NET runtime needs to be installed. Place both
files together and then follow the [user guide](docs/USER_GUIDE.md). For
Discovery and WAPI, UDP and TCP port 2222 must be reachable on the isolated
control network.

For a safe demonstration without consoles:

```powershell
.\scripts\run.ps1 -Demo
```

## Building and testing

For development you need Windows x64, .NET SDK 10.0.302, CMake, and the x64
MSVC toolchain from Visual Studio 2022. The official WAPI files and the
associated SLA are included in the repository.

```powershell
.\scripts\test.ps1 -Configuration Release
.\scripts\build.ps1 -Configuration Release
```

`test.ps1` builds first by default and then runs 77 core tests, 76
integration/fault-injection tests, the native helper self-test, and 14 UI
automation tests. The UI tests require an unlocked interactive Windows desktop
session.

`build.ps1` repeats the tests, publishes the self-contained win-x64 app, and
creates:

- `artifacts\WingSync-win-x64-internal-evaluation\`
- `artifacts\WingSync-portable-win-x64-internal-evaluation.zip`
- `artifacts\WingSync-portable-win-x64-internal-evaluation.zip.sha256`

The build also runs the full UI suite against the exact published single-file
app and again against the app extracted from the zip. The build manifest
records source hash, test evidence, and Authenticode status. Without a trusted
code-signing certificate, the status intentionally remains
`internal-evaluation-unsigned`.

Use `-SkipTests` or `-SkipBuild` only when the relevant results are already
available for the exact same source and configuration.

## Documentation

- [User guide](docs/USER_GUIDE.md)
- [Architecture and safety boundaries](docs/ARCHITECTURE.md)
- [Test and acceptance report](docs/TEST_REPORT.md)
- [WAPI license information](LICENSES/README.md)
