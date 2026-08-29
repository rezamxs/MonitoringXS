# ADR 0004: Restricted privileged ETW broker service

- Status: proposed checkpoint
- Date: 2026-07-28
- Scope: Network and Physical disk ETW only

## Decision

Keep `MonitoringXS.App` `asInvoker` and move the shared ETW session behind an
automatically started Windows Service, `MonitoringXS.PrivilegedEtwBroker`, running as
`LocalSystem`. LocalService reached `TraceEventSession.EnableKernelProvider`
for `MonitoringXS.KernelMetrics.v1` but returned Win32 5
(`ERROR_ACCESS_DENIED`); the same fresh binary succeeded under LocalSystem.
UAC is required only for installation/setup.
`MonitoringXS.ElevatedHelper` remains the existing on-demand single-operation
helper and is not repurposed.

The service exposes a version-1 named pipe with a fixed name and strict framed
JSON protocol. Operations are limited to hello, read physical-disk batch, and
read network batch. Requests accept only a bounded list of PID plus
`StartTimeUtc` identities. The service authorizes the exact app executable,
interactive session, user SID, and each process lifetime before and after the
ETW read, then filters responses to those identities.

## Security and resource controls

The pipe name is derived from the client user SID, optional logon SID, and
interactive session. It is created with `NamedPipeServerStreamAcl.Create` and
an explicit protected DACL: owner LocalSystem (`S-1-5-18`), Network SID
denied, LocalSystem and the dedicated service SID allowed full control, and
only the configured user/logon SID allowed `ReadWrite|Synchronize`. The
application authorization layer prevents a same-machine client from reading
another user's processes. Frames are capped at 64 KiB requests and 4 MiB
responses; process lists cap at 2,048 entries; one connection and one
serialized request are allowed. Connect, request, and idle deadlines are 2,
5, and 15 seconds. Cancellation, reconnect, broker restart, disposal, and
bounded ETW queues are deterministic and observable.

Expected failures are structured `Unavailable`, `Partial`, access denied, or
unsupported results. The broker never accepts arbitrary commands, paths,
providers, sessions, shell strings, or process launches, and never returns
global counters that could leak other users.

## Threat model

The pipe is local but may be probed by another process, another user, a
stale/reused PID, a malformed client, or a client that hangs. The DACL reduces
transport exposure; token/SID/session/executable checks and per-read lifetime
revalidation provide authorization; strict framing, JSON field rejection,
caps, serial work, and timeouts bound denial-of-service. PID reuse is rejected
when the UTC start time differs. A production installer must additionally
install ACL-protected, signed binaries and preserve the fixed executable
identity; this checkpoint does not claim publisher authentication for an
unsigned development build.

## Alternatives considered

1. Keep direct ETW in the app: rejected because ordinary users can receive
   `AccessDenied` on the shared kernel session and the UI must not elevate.
2. Repurpose `MonitoringXS.ElevatedHelper`: rejected by the project contract;
   it is an on-demand single-operation process, not a long-lived broker.
3. `LocalService`: rejected for production. It is preferred by least privilege,
   but the exact `TraceEventSession.EnableKernelProvider` call returned Win32 5
   after a successful protocol-v1 handshake. No broader user privilege or pipe
   ACL was granted.
4. Driver, undocumented API, or arbitrary RPC: rejected as unnecessary and
   higher risk.

## Installer requirements

The eventual installer must request elevation only while creating/removing the
automatically started service, copy signed binaries to an ACL-protected system
location, set the service identity to LocalSystem with a dedicated service SID,
keep it non-interactive, and remove temporary setup
artifacts on failure. Normal app launch must remain asInvoker. CI builds and
tests must not require elevation or an installed service.

## 2026-07-29 final identity probe

The same fresh Release app and broker completed protocol v1 under LocalService.
The broker then reached `TraceEventSession.EnableKernelProvider` for
`MonitoringXS.KernelMetrics.v1` and recorded native Win32 status 5
(`ERROR_ACCESS_DENIED`). This was an ETW provider authorization failure, not a
pipe failure.

The matching broker was then installed temporarily as LocalSystem. Its explicit
pipe owner was LocalSystem; Network SID remained denied; only LocalSystem, the
dedicated service SID, and the configured user were allowed. The pipe existed,
client/server names matched, protocol v1 completed, and `logman query -ets`
reported the session running. Controlled PID-plus-`StartTimeUtc` activity
produced two Physical disk events (16,384 bytes read and 1,048,576 bytes
written) and 19 Network events (five send and 14 receive; 1,048,901 source-send
bytes and 1,052,757 source-receive bytes). Restart recovery completed with a new
service instance. Cleanup left no Monitoring XS process, service, ProgramData
validation directory, or ETW session.
