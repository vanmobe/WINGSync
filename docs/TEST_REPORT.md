\
# Test and acceptance report

Last document update: **2026-07-26**.

A production build for live audio may be released only when all required
physical acceptance rows are **PASS** and every temporary hardware change has
been read back and restored exactly. A green automated suite does not replace
that console acceptance.

## Automated evidence

| Suite | Result | Coverage / evidence |
|---|---:|---|
| Core unit tests | **PASS · 77/77** | domain, configuration validation, scope/token matching, mappings, risk classes, planner phases, and sidechain guards |
| Simulator and fault-injection tests | **PASS · 76/76** | initial sync, confirmation, exact scalar certification, processor guards, readback, emergency-stop races, drift, echo, polling budget, queue overflow, reconnect generations, concurrency, cache reset, and support redaction |
| UI automation | **PASS · 14/14** | real WPF/UI Automation main flows, accessibility, minimum size, persistence, offline cache, active-write status, live and high-risk confirmation, and keyboard shortcuts |
| Native helper self-test | **PASS** | `WingSync.WapiHost.exe --self-test` |
| Release managed build | **PASS** | 0 warnings, 0 errors; warnings-as-errors and latest-recommended analyzers |

The three managed test suites are dependency-free executable test harnesses.
The current full command is:

```powershell
.\scripts\test.ps1 -Configuration Release
```

That command builds managed and native code, runs 77 core tests and 76
integration tests, starts the native self-test, and finishes with 14 UI
automation tests. UI Automation requires an unlocked interactive Windows
desktop session; a headless or locked session is not valid UI test evidence.

### Key regression coverage

- Live initial sync cannot write without current confirmation.
- A source or target change during a preview invalidates that preview.
- A transport-generation change immediately before `SetMany` forces a new
  snapshot/confirmation.
- Reconnect, active worker batch, and confirmation are serialized; duplicate
  reconnect triggers do not cause duplicate writes.
- An emergency stop during an already-sent confirmation batch allows only that
  batch and readback to end terminally and blocks every following unit.
- Initial live execution and reconnect catch-up explicitly report that writes
  and readback are active.
- Real source events remain authoritative during simultaneous target drift.
- Readback mismatch, identity mismatch, and queue overflow pause fail-closed.
- Slow periodic reads may let an already-sent WAPI command finish and do not
  cause an unnecessary helper recycle or reconnect storm.
- Model and delay transactions keep the processor safely off, rewrite reset
  side effects, and restore the desired enable value only after complete exact
  group readback.
- A persistent conflict between a model node cache and the exact model scalar
  pauses without a target write.
- An unresolvable gate or dynamics sidechain blocks the entire affected
  processor group.
- Cache reset is linear with pending writes; dispose drains already accepted
  work.
- An old WAPI helper reader cannot publish into a new process generation.
- The support bundle redacts configuration, identity, addresses, local paths,
  and parameter values; malformed source content is omitted.

### UI scenarios

The 14 passing scenarios are:

1. startup defaults and accessibility contract;
2. navigation to all four pages;
3. mapping, AUX, invalid input, and start validation;
4. configuration persistence after restart;
5. dry-run start/stop/restart and editor lock;
6. safe default choice **No** for live preview;
7. truthful active-write status and emergency stop during confirmed live
   apply;
8. separate high-risk review and confirmation;
9. closing during an active session;
10. complete and readable redacted support bundle;
11. reachable primary actions at minimum window size;
12. visible identities, truthful metrics, and revoked approvals after topology
    changes;
13. offline cache after restart without replay;
14. `Ctrl+1`–`Ctrl+4` and emergency stop `Ctrl+Shift+S`.

## Physical WING acceptance

On 2026-07-26, the bounded identity, read-only, and write/readback/recovery
tests were run again on two serial-pinned WING variants with firmware 3.1. The
harness chose a silent channel only after a fresh read-only safety selection.
Four allowed stimuli were read back exactly on source and target and then
restored exactly; the write-through journal contains one `recovery-complete`
marker.

| Physical test | Status | Required evidence |
|---|---:|---|
| Fresh discovery and exact identity pin of both consoles | **PASS** | fresh pair discovery; WAPI serial rebound exactly to each role after connect |
| Two independent WAPI helper connections | **PASS** | `$SYSCFG` 26, `$STAT` 32, four keepalives in ≥12 s, and clean disconnect |
| Read-only scoped snapshot | **PASS** | 286 CH40 items per console; no writes |
| Live `CUST` write/readback/restore | **PASS** | only approved silent-channel token; exact restore |
| Live disabled `EQ` scalar/readback/restore | **PASS** | channel demonstrably silent; processor off; exact restore |
| Live disabled `GATE` scalar/readback/restore | **PASS** | channel demonstrably silent; processor off; exact restore |
| Live disabled `DYN` scalar/readback/restore | **PASS** | channel demonstrably silent; processor off; exact restore |
| Physical cable interruption and reconnect/resnapshot | **PENDING** | both directions, no stale replay or duplicate write |
| Physical latency/event-storm measurement | **PENDING** | p95/p99 and convergence with bounded memory |
| Endurance test | **PENDING** | at least 8 hours, no unplanned writes, leaks, or silent faults |

A device previously observed on the network or a successful ping is at most
inventory information and does not count as a PASS in this table.

## Safe hardware procedure

The read-only test can be started explicitly with:

```powershell
.\scripts\test.ps1 -Configuration Release `
    -LiveReadOnly `
    -FohSerial '<exact-foh-serial>' `
    -StageSerial '<exact-stage-serial>'
```

The live write test is intentionally double-locked:

```powershell
.\scripts\test.ps1 -Configuration Release `
    -LiveWrites `
    -AcknowledgeLiveWrites `
    -FohSerial '<exact-foh-serial>' `
    -StageSerial '<exact-stage-serial>'
```

Run that command only in a maintenance window, after show backups, and with
both outputs physically safe. Before every write, the harness must prove that
CH40 is silent, disabled, and unrouted on both consoles. The allowlist
contains only `CUST` and one scalar each from a disabled `EQ`, `GATE`, and
`DYN`. Before every mutation, a write-through recovery journal is saved first;
the `finally` phase restores source and target exactly and reads back the
restore.

The application itself uses only WAPI through UDP/TCP 2222. The live
acceptance harness additionally uses UDP 2223 to the FOH console as an
independent, strictly allowlisted OSC stimulus; that path is not part of the
production app. Recovery journals are stored under
`%LOCALAPPDATA%\WingSync\HardwareTestRecovery`.

A test with an incomplete preflight, unexpected identity, changed original
value, non-approved token, or failed restore is **FAIL**, even when the write
itself technically succeeded. A recovery journal then remains a blocking action
before the consoles may be used again.

## Acceptance boundaries

- No write outside the selected direction, mapping, scopes, and explicit test
  allowlist.
- Every live write has matching readback or immediately causes a visible,
  blocking problem.
- p95 propagation < 100 ms and p99 < 250 ms on the acceptance network.
- 1,000 events converge within 2 seconds with bounded memory.
- After network recovery, back online within 10 seconds, after exact identity
  check and fresh snapshots, without stale replay.
- Cache corruption leads to quarantine/rebuild and never to a console write or
  application crash.
- After the endurance test, helper processes, handles, queue use, log drop,
  and memory use are stable.

## Release decision

**NO-GO for production live use while the physical reconnect, event-storm, and
endurance tests are still PENDING.** The current unsigned portable build is an
internal evaluation release. Dry run, simulator, and UI workflow may still be
used to prepare configuration and operating procedures.
