# WapiHost

`WingSync.WapiHost.exe` is the native, one-process-per-console bridge between WingSync
and x32ram's official WING API. Only its line protocol is written to stdout;
vendor diagnostics and self-test output go to stderr.

## Protocol

Input commands are UTF-8 lines:

```text
HELLO
CONNECT|id|ipv4
DISCONNECT|id
SNAPSHOT|id|tokenName
SET|id|tokenName|type|base64Value
SETMANY|id|tokenName|type|base64Value|tokenName|type|base64Value|...
PING|id
QUIT|id
```

An empty `ipv4` asks WAPI to discover a console. Token names accept both WAPI
JSON names (`ch.1.fdr`) and enum-style names (`CH_1_FDR`). Values are standard,
canonical base64. `I` is a signed 32-bit decimal, `F` is a finite float, and
`S` is an arbitrary NUL-free byte string (normally UTF-8).

Output lines are:

```text
READY
OK|id
ERROR|id|code|base64Message
STATE|id|status|base64Detail
ITEM|id|tokenName|type|base64Value
END|id|count
EVENT|tokenName|type|base64Value
```

The concrete state line has four fields:
`STATE|id|status|base64Detail`. The alternatives in the synopsis above denote
the allowed value of `status`.

`SNAPSHOT` accepts either a scalar token or a node. A scalar produces one
`ITEM`; a node produces every scalar returned by WAPI, followed by `END`.
Unsolicited WING changes are continuously emitted as `EVENT` records.

`SETMANY` accepts up to 512 token/type/value triples within the 1 MiB command
limit. It validates the complete batch (including unknown, node, read-only and
duplicate tokens) before issuing exactly one `wSetNodeFromTVArray` call, so a
validation failure cannot partially send a batch.

Each `id` is a per-command correlation identifier; connection ownership is the
helper process itself, so successive commands normally use different ids.

One reader thread only reads stdin. The main loop exclusively owns every WAPI
call, stdout write, keepalive operation, snapshot, mutation, and event drain.

## Standalone build and test

From a Visual Studio x64 developer shell:

```powershell
cmake -S native -B build/native -A x64
cmake --build build/native --config Release
ctest --test-dir build/native -C Release --output-on-failure
```

The release uses MSVC's static CRT and therefore needs no Visual C++
redistributable DLLs; its only runtime imports are Windows system libraries.

The self-test exercises base64 and the complete WAPI token index without
connecting to a WING:

```powershell
build\native\Release\WingSync.WapiHost.exe --self-test
```
