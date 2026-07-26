# Architecture

## Purpose and invariants

WingSync synchronizes only explicitly selected global WING sections and
channel mappings from one authoritative source console to one target console.
The default direction is FOH → stage. The reverse direction is possible, but
bidirectional synchronization is rejected by the validator.

The safety invariants are:

1. dry run is the default and sends no parameter writes;
2. live requires two visible, pinned, and re-verified hardware serial numbers;
3. source and target are freshly read for every initial or recovered
   reconciliation;
4. only the intersection of selected scope, valid mapping, and permitted risk
   class may be executed;
5. every live write requires readback; a mismatch pauses fail-closed;
6. configuration and transport generations are re-checked immediately before
   dispatch;
7. cache data is never input for a write plan and is never replayed.

## Components

```text
WPF UI
  │  setup, status, diff confirmation, activity, recovery
  ▼
SyncCoordinator ── WritePlanner / ScopeCatalog / ConfigValidator
  │                     │
  │                     └─ mapping, risk class, model and sidechain guards
  ├─ FOH IWingSession ── WingSync.WapiHost.exe A ── TCP/2222 ── WING FOH
  ├─ MON IWingSession ── WingSync.WapiHost.exe B ── TCP/2222 ── WING stage
  ├─ WingStateCacheSink ── atomic local cache
  └─ SyncDiagnosticsHub ── bounded JSONL audit trail
```

- `WingSync.Core` contains domain models, validation, token/scope matching,
  write planning, and the stateful coordinator.
- `WingSync.Infrastructure` contains discovery, helper-process transport,
  simulator, configuration/cache, JSONL diagnostics, and support export.
- `WingSync.App` is the Windows WPF interface.
- `WingSync.WapiHost.exe` is a statically built x64 MSVC helper around the
  official WAPI C API.

## Why one helper process per console

The WAPI C API uses process-global connection state and does not provide an
independent session handle. Two connections in one process could affect each
other's global state. WingSync therefore starts one helper process per
physical console and communicates with it through a strict stdin/stdout
protocol.

A new connection always replaces the previous helper generation. Stdout and
stderr readers for a stopped process are awaited before a replacement becomes
active so that old output cannot land in a new session. Timeout, protocol
error, helper crash, or unexpected disconnect marks the session as faulted and
triggers the fail-closed reconnect flow.

## Discovery and identity

Discovery sends exactly `WING?` via UDP/2222 on active IPv4 adapters and
deduplicates responses. A `DiscoveredWing` contains IP, name, model, serial
number, firmware, and observation time. Native WAPI uses fixed TCP port 2222;
a different configured port is a validation error.

Live connections use a `WingEndpoint` with an expected serial number. The
identity verifier checks both IP and serial:

- for the connection test;
- for the first live snapshot;
- after every reconnect;
- for source and target individually.

The same physical console may not fulfill both roles. Discovery or cached
metadata alone never grants write permission.

## Reconciliation and concurrency

The first and every recovered synchronization proceeds as follows:

1. block event intake and live dispatch;
2. connect both isolated helpers and verify both identities;
3. freshly read all required source and target nodes;
4. capture source, target, and transport generations with the snapshot;
5. filter, map, and compare values into a deterministic diff;
6. show the diff in dry run or wait for explicit live confirmation;
7. re-check the generations at batch start and immediately before `SetMany`;
8. execute only allowed writes and read them back;
9. release event intake and process events that were safely buffered during the
   snapshot.

A `reconcileGate` serializes initialization, confirmation, reconnect, and
worker batches. Because of that, a reconnect cannot overlap an active
confirmation or write batch. A source change that invalidates a preview
produces a new preview; it may not be executed with an old confirmation.
Target drift is re-planned against a fresh scalar from the authoritative
source.

The event queue is bounded. Events are coalesced briefly per token, with real
source events taking priority over synthetic target drift. A fingerprint
window suppresses echoes of locally written target values. On queue overflow,
readback mismatch, or safety inconsistency, live mode is disarmed and intake is
paused instead of losing data.

## Scope model

The UI follows the global WING Library sections:

| UI scope | Internal `SyncScope` | Canonical token family |
|---|---|---|
| `CUST` | `Cust` | name, color, icon, led |
| `TAGS` | `Tags` | tags |
| `CONN` | `Conn` | `in/conn`, input selection |
| `IN` | `In` | channel trim, balance, phase |
| `FILTER` | `Filter` | `flt` |
| `DELAY` | `Delay` | input delay |
| `GATE` | `Gate` | `gate`, `gatesc` |
| `DYN` | `Dyn` | `dyn`, `dynxo`, `dynsc` |
| `PRE` | `Pre` | `preins` |
| `POST` | `Post` | `postins` |
| `EQ` | `Eq` | `eq`, `peq` |
| `PAN` | `Pan` | pan and width |
| `MAIN` | `Main1`–`Main4` | `main/1`–`main/4` |
| `BUS` | `Send` | `send` |
| `FADER` | `Fdr` | `fdr` |
| `MUTE` | `Mute` | `mute` |
| `CONFIG` | `Config` | process order, tap, solo-safe, monitor |

This keeps the operator labels `MAIN`, `BUS`, and `FADER` aligned with the
global WING UI while the internal enum accurately reflects the WAPI token
families.

The safe default selection is `CUST`, `FILTER`, `DELAY`, `GATE`, `DYN`, and
`EQ`. `TAGS`, `CONN`, `PRE`, `POST`, `MAIN`, `BUS`, `FADER`, `MUTE`, and
`CONFIG` are high risk and require separate approval. `IN` and `PAN` are not
classified as high risk, but remain conservatively off by default.

Only channel paths for CH 1–40 and AUX 1–8 are rewritten. Read-only `$` tokens
and unknown paths remain outside the plan. Physical head-amp gain, phantom
power, and `/io/in` routing are explicitly outside `IN`. Embedded gate and
dynamics sidechain references must be mapped unambiguously; otherwise the
entire affected processor group is blocked. For model changes, the engine
plans safe phases: processor off, model, parameters, and finally the desired
on/off state.

## Dry run and live confirmation

Dry run uses the same fresh snapshots, planner, and safety guards as live
mode, but marks all executable differences as preview and does not call
WAPI set. Metrics distinguish previews, verified writes, and blocked
Differences.

Live starts in `AwaitingConfirmation`. The operator sees source, target,
serials, direction, scopes, mapping count, total diff, executable and blocked
counts, and the breakdown per scope. The default action is to refuse. When a
high-risk scope is active, a second independent confirmation follows. Changing
identity, IP, direction, scope, or mapping revokes live/high-risk approval,
returns the configuration to dry run, and invalidates the connection test.

## Cache and storage

Configuration, cache, support files, and JSONL logs are stored under
`%LOCALAPPDATA%\WingSync` by default; tests and support can use an explicit
Data directory.

Configuration and cache are stored with a temporary file, flush, and atomic
replacement. A cache partition includes console serial, model, firmware,
parameter path, type, value, observation time, and connection epoch. Writes to
the cache are serialized together with reset and dispose.
Corrupt, incompatible, or identity-mismatched files are isolated; a valid
backup may be restored for offline display.

There is intentionally no path from cache to planner or WAPI dispatch. The UI
can show the number of values and the age of a known snapshot without a console
connection, but marks it as offline/stale. A reconnect always starts with
identity verification and fresh console data.

## Diagnostics and privacy

Diagnostics use bounded, rotating JSONL logs. A logging problem must not block
the real-time engine; dropped entries remain visible as a counter.

A support bundle is written to a temporary zip file and then moved into place
atomically. It contains a sanitized configuration structure, sanitized logs,
and a short status diagnosis. The export redacts:

- IPv4 and IPv6 addresses;
- WING serial numbers;
- local user profile paths;
- parameter and readback values;
- raw helper or verification messages that may contain values.

Cache files and console snapshots are not included. The export asks for
permission first and does not overwrite an existing bundle.

## Build and distribution boundary

The managed code targets .NET 10 for Windows and builds with nullable,
latest-recommended analyzers, and warnings-as-errors. The release publishes
the WPF app self-contained and single-file for win-x64. The native WAPI helper
remains a separate file next to `WingSync.exe` because that process boundary is
part of the session model. The portable zip also contains the user guide and
WAPI license files.
