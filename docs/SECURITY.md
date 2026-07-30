# Security architecture

## Trust boundaries

Process metadata, command lines, executable paths, signatures, network destinations, imported mappings, database contents, and elevated-helper requests are untrusted or sensitive inputs.

## Controls

- Run unelevated by default; no service and no kernel driver.
- Do not invoke a shell for application actions.
- Validate executable identity again immediately before destructive or privileged operations.
- Deny actions against critical/protected Windows processes regardless of UI attribution.
- Avoid unsafe DLL search by using system-qualified paths and safe library-loading flags.
- Sanitize logs; omit command-line arguments, credentials, tokens, and network query strings.
- Store history locally and provide deletion controls.
- Exporting advanced diagnostics requires a sensitivity warning and explicit user action.
- Attribution overrides contain executable paths and are sensitive local data. The JSON store validates fully qualified paths and dispositions, caps entry count, writes through a unique temporary file plus atomic replacement, and never logs override contents.
- Authenticode inspection reports embedded certificate presence and signer metadata only; it does not present certificate presence as trust-chain validation.
- Physical-disk ETW enables only disk-I/O, disk-I/O-init, and thread keywords. Monitoring XS does not subscribe to file-name events, store paths, request stack traces, or pass raw QPC timestamps into application models. IRP correlation is bounded; limited thread handles used for pre-existing threads are closed immediately.
- The shared kernel session also enables TCP/IP network events. It retains only PID, UTC timestamp, direction, transport, address-family category, and byte count. Packet payloads, URLs, credentials, hostnames, ports, and local or remote addresses are not retained or logged.
- TCP and UDP endpoint counts come from owner-PID IP Helper tables with a 16 MiB input cap. Addresses and ports in those tables are skipped rather than copied into application models.
- The ETW session uses `NoRestartOnCreate`: an existing same-name session is reported as unavailable and is never stopped or replaced. The app never requests elevation automatically.
- ETW loss discards the current batch and thread map before attribution; PID reuse is rejected by comparing UTC-normalized event and process-start timestamps. Network PID 0/4, unknown, outside-application-set, and pre-start events remain unattributed and are never reassigned from destination, foreground activity, or application size.
- Physical-disk diagnostics retain only aggregate counters, status, latency, and the last event timestamp. They do not add file paths, file names, command lines, stack traces, or event payload logging.
- Network diagnostics likewise retain aggregate event/byte/category counts, queue/loss/failure state, latency, and the last event timestamp. They do not retain network destinations or packet content.
- GPU sampling reads only Windows performance-counter instance names and numeric engine/dedicated/shared values. It does not load code into another process, invoke vendor tools, record window content, or start another ETW session.
- GPU attribution accepts a counter PID only after its absolute UTC process creation `FILETIME` matches the discovered `ProcessInstanceId`. An inaccessible, exited, reused, stale, or merely related descendant PID is never assigned by guesswork.
- The native GPU reader bounds every PDH buffer allocation to 64 MiB and 65,536 items, validates item-array/string bounds before dereferencing native memory, and quarantines a changed PID lifetime until a complete enumeration observes the old instance absent. Concurrent capture and disposal share one lock and disposal is idempotent.
- GPU diagnostics retain adapter LUID, physical index, engine type/index, aggregate counts, availability, and timing. They do not add command lines, paths, rendered frames, shader content, or application data.
- The one-time elevated validation was launched manually from Administrator PowerShell. It did not change the application manifest, add startup elevation, or install a service, driver, or persistent helper; normal execution still degrades honestly without elevation.
- The Milestone 3A unelevated smoke returned `AccessDenied` for the shared physical-disk/network kernel session. No automatic elevation was attempted, and `logman` found no active Monitoring XS session.

## Privileged ETW broker (Phase 3B)

The broker is a restricted, non-interactive service under `LocalSystem`. LocalService is unsupported because the exact `TraceEventSession.EnableKernelProvider` call for `MonitoringXS.KernelMetrics.v1` returned Win32 5 (`ERROR_ACCESS_DENIED`); the same fresh binary succeeded under LocalSystem. Its per-user/session named pipe is created with `NamedPipeServerStreamAcl.Create` and a protected explicit DACL: owner LocalSystem (`S-1-5-18`), Network SID denied, LocalSystem and the dedicated service SID allowed full control, and only the configured interactive user SID (or logon SID when present) allowed `ReadWrite|Synchronize`. Application-level authorization still checks token SID, session, exact executable image, and every PID plus `StartTimeUtc`. Requests are versioned JSON frames with strict fields, 64 KiB request/4 MiB response caps, 2,048-PID cap, one connection, serial request gate, and 2/5/15-second connect/request/idle deadlines. Only hello, network read, and physical-disk read are implemented; no provider/session/path/command/process-launch input is accepted. Responses are filtered to authorized PID lifetimes and contain no global counters. The broker has no outbound-network operation. See [ADR 0004](adr/0004-privileged-etw-broker-service.md).

The original authorization failure was the missing explicit ACE for the current interactive user on the per-user pipe; the client therefore received `UnauthorizedAccessException` during connect. The fixed client connects and completes protocol v1; a later unauthorized executable is rejected after handshake with broker error code 4. No `Everyone` ACE is used.

The unelevated client queries SCM only for the fixed service name before
classifying a connection failure. Normal UI exposes an allowlisted message:
service not installed, service stopped, connection failed, protocol mismatch,
ETW unavailable, or no attributed activity yet. Arbitrary broker exception
text, executable paths, pipe names, and SIDs are not rendered in normal UI;
full native evidence remains in local validation diagnostics.

The tracked development/operator entry point is
`scripts/privileged-broker/Manage-PrivilegedBroker.ps1`. Install and Remove
require elevation; Status and normal app launch do not. The script publishes the
matching Release broker, verifies LocalSystem/automatic-start/path/protocol
state, and removes only the fixed service and
`%ProgramData%\MonitoringXS\PrivilegedEtwBroker`. It does not grant broad pipe
access, accept provider/path/command input, touch history/settings, or elevate
`MonitoringXS.App`.

The final identity probe first completed the version-1 pipe handshake under
LocalService, then captured Win32 5 from
`TraceEventSession.EnableKernelProvider`. A matching LocalSystem deployment
created the session, delivered independently attributed Network and Physical
disk events, recovered after restart, and removed all service, process, file,
and ETW artifacts. LocalSystem is therefore limited to this broker and required
only for the documented kernel ETW operation.

## SQLite history

History stays local under `%LOCALAPPDATA%\MonitoringXS\history.db`. SQL is
parameterized; schema migrations are versioned and transactions are crash-safe.
Only logical application metadata, PID-plus-`StartTimeUtc` lifetime keys,
numeric metric values, UTC timestamps, availability, and bounded diagnostic
detail are stored. Packet payloads, URLs, hosts, IPs, ports, command lines,
secrets, and raw ETW events are excluded. WAL is enabled when supported and
falls back with a diagnostic when unavailable. Corrupt files are quarantined
with a `.corrupt-*` suffix before recreation; locked, read-only, full-disk, and
size-limit failures remain explicit and never become zero values.
`Microsoft.Data.Sqlite` 10.0.10 is used with the compatible
`SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 security update centrally pinned; the
deprecated vulnerable 2.1.11 native library is not resolved.

The History page only selects logical application IDs already present in this
local store and queries the requested UTC range off the UI thread. It exposes
numeric summaries, availability states, and local-time chart labels; it does
not add packet data, URLs, hosts, addresses, ports, command lines, or secrets.
Unavailable and partial rows remain gaps, so the page cannot turn missing
storage or broker data into a fabricated zero.

## Per-user settings

Settings stay under `%LOCALAPPDATA%\MonitoringXS\settings.json` as a small
version-1 JSON document. It contains only the live interval, history-retention
hours, and System/Light/Dark theme. It stores no credentials, tokens, SIDs,
Broker ACLs, executable paths, command lines, or network data. Values are
allowlisted, writes use a temporary file plus atomic replacement, and corrupt
or invalid files are quarantined before safe defaults are used. A newer
document version fails closed without overwriting it. Normal UI receives only
allowlisted Broker states and never installs, removes, restarts, or elevates
the service. See [ADR 0006](adr/0006-versioned-per-user-settings.md).

## Elevated helper design

The helper will accept a versioned request containing only an operation enum and validated identifiers. It will reject unknown fields/operations, verify the caller and target, perform one allow-listed operation, return one structured response, and terminate. General process launch, arbitrary paths, and shell strings are forbidden.

## Reporting

See the root `SECURITY.md` for responsible disclosure. Do not include sensitive diagnostic data in public issues.
