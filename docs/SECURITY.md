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

## Elevated helper design

The helper will accept a versioned request containing only an operation enum and validated identifiers. It will reject unknown fields/operations, verify the caller and target, perform one allow-listed operation, return one structured response, and terminate. General process launch, arbitrary paths, and shell strings are forbidden.

## Reporting

See the root `SECURITY.md` for responsible disclosure. Do not include sensitive diagnostic data in public issues.
