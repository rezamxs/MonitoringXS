# Troubleshooting

## `No .NET SDKs were found`

Install the .NET 10 SDK (the runtime alone is insufficient) and restart the terminal. Verify with `dotnet --list-sdks`.

## WinUI targets or templates are missing

In Visual Studio Installer add the stable **WinUI application development** workload and a current Windows SDK. Do not select preview Windows App SDK packages for production.

## Restore cannot reach NuGet

Confirm `https://api.nuget.org/v3/index.json` is reachable and that an authenticated proxy is configured in NuGet. Do not commit machine credentials.

## Counters are unavailable

Monitoring XS must keep other metrics running and show `Unavailable`. Confirm the Windows performance counter service/ETW permissions and graphics driver are healthy. Never substitute zero.

## Live metrics stop changing after the Broker starts

Rebuild both app and Broker from the same checkout. Older builds could reject a
nonempty Broker event batch because the JSON constructor parameter `timestamp`
did not bind to `TimestampUtc`; the refresh then retried without publishing a
new snapshot. Current builds bind both event types correctly, isolate each
collector behind a 750 ms timeout, and keep the single non-overlapping refresh
loop alive after transient collector or history failures.

## Access denied for a process

Protected or higher-integrity processes may expose limited metadata. This is expected degradation; the primary app should remain unelevated.

## Physical disk (ETW) is unavailable

Kernel ETW can require administrator rights or membership in **Performance Log Users**. Monitoring XS does not open UAC automatically: it shows `Access denied` while CPU, memory, and Process I/O continue. For a deliberate manual diagnostic, start PowerShell as Administrator and launch the app from that shell.

The kernel provider must be enabled before accessing the TraceEvent source/parser; current builds enforce this ordering. If an older build reports `The kernel provider must be enabled first and only once in a session`, rebuild before retrying.

Check a suspected stale session from an Administrator shell with `logman query MonitoringXS.KernelMetrics.v1 -ets`. The app deliberately will not replace an existing same-name session. Stop it only after confirming that no Monitoring XS process owns it. ETW loss or local queue overflow is shown as `Partial` because displayed mapped values are lower bounds; unattributed events remain visible in diagnostics without contaminating mapped applications.

## Network (ETW) is unavailable

Network and physical-disk events share the fixed, neutral, versioned `MonitoringXS.KernelMetrics.v1` kernel session so the app does not start competing kernel sessions. The same access-denied and session-conflict checks apply. Normal execution remains unelevated and the app must keep CPU, memory, and Process I/O available when kernel ETW is unavailable.

`Partial` means observed rates are lower bounds because ETW lost events, the local bounded queue dropped records, parsing failed, an unsupported event version was detected, or the collector restarted. Retained-session totals stay partial after confirmed incompleteness. TCP or UDP counts can be unavailable independently when Windows cannot return a complete IPv4 and IPv6 owner-PID table snapshot.

In Advanced Mode, check the separate event rate, queue depth/capacity, local drops, ETW loss, processing failures, unsupported versions, attributed/unattributed categories, and last-event time. A large `outside app set` count is not automatically a failure: the shared kernel source sees system-wide traffic, while cards intentionally include only confidently attributed user applications. PID 0/4 and unknown events are never guessed into a card.

Download/Receive and Upload/Send are packet bytes observed at the kernel ETW event point. They can differ from browser tools, Task Manager, a router, or ISP accounting because those tools may count payload, wire traffic, retransmissions, loopback, or offloaded traffic differently.

## GPU is unavailable or stays at zero

Monitoring XS uses the Windows `GPU Engine` and `GPU Process Memory` performance counter sets. They require a WDDM 2.x-capable driver. Update the graphics driver from the device or system vendor and confirm that Task Manager's GPU columns work. Missing counter objects are shown as `Unsupported`; do not repair this by substituting zero.

A healthy zero means Windows exposed the provider but no matching engine instance existed for that application at the sample time. Hardware acceleration can be disabled, a workload can use software rendering, or a browser/video application can place work in a helper process. Monitoring XS attributes a helper only when its PID and UTC start time are available and the normal application attribution includes it. An inaccessible or ambiguous helper remains partial or unavailable.

On an integrated GPU, zero dedicated memory can be valid while shared memory is nonzero. On a multi-adapter system, Advanced diagnostics show the adapter LUID and physical index of the busiest engine. Virtualized activity can be owned by a host process such as `vmmem`, and a remote session can expose different counters.

A controlled browser or WebGL workload can still show `GPU 0.0%, 0 B dedicated · 0 B shared` when the browser uses software rendering, a protected/sandboxed path, or a driver that does not publish a per-process GPU instance. This is an honest unavailable/zero observation for that process, not a fallback estimate. A different workload such as VLC may expose real `3D` engine values on the same machine.

Per-process dedicated/shared values are Windows-reported attribution values, not a unique total of physical VRAM. Cross-process shared allocations can be counted in more than one process. Microsoft also documents an affected Windows 10 case where a per-process GPU memory value can falsely keep increasing; compare the Task Manager Performance tab or a WPR/WPA trace before treating that counter as proof of a leak.

## Attribution overrides are unavailable

Monitoring XS reports an invalid, inaccessible, or unsupported `%LocalAppData%\MonitoringXS\attribution-overrides.json` file as unavailable and continues without fabricating mappings. Preserve the file for diagnosis or move it aside while the app is closed; the app will create a new versioned document after the next explicit override change.

## The app closes when advanced details are expanded

Older builds could synchronously redraw the CPU sparkline from `SizeChanged` while the advanced Expander was changing layout. WinUI reported `LayoutCycleException` and terminated with `0xc000027b` in `Microsoft.UI.Xaml.dll`. Current builds coalesce resize redraws and dispatch them after the active layout pass. Rebuild the full Release solution and confirm it with `scripts\validation\Invoke-MonitoringXsUiAutomationStress.ps1`; do not suppress the fail-fast or mark the XAML exception handled.

## Corrupt local history

Close Monitoring XS before inspecting `%LOCALAPPDATA%\MonitoringXS\history.db`.
The backend automatically quarantines a corrupt file as `.corrupt-*` and creates
a new version-2 database. Locked/read-only/full-disk failures are reported in
storage diagnostics and live metrics continue without fabricated zeros. Do not
manually overwrite the database while Monitoring XS is running.

## History is empty, partial, or unavailable

History lists only logical applications already persisted in
`%LOCALAPPDATA%\MonitoringXS\history.db`. Run the app long enough to collect
samples, then use Refresh. A blank selected range is not a zero. `Partial` and
`Unavailable` samples, broker outages, and application relaunch boundaries are
drawn as gaps. If the database is locked, read-only, corrupt, or full, History
reports the database/query state while live monitoring continues independently.

# Privileged ETW broker

If Network or Physical disk (ETW) shows `Unavailable` or `Partial`, verify that `MonitoringXS.PrivilegedEtwBroker` is installed as `LocalSystem` and running. LocalService is unsupported because `TraceEventSession.EnableKernelProvider` returned Win32 5 in the validated path. Restarting the service is safe: the client reconnects, reports `Partial` for the first post-restart response, and never fabricates zero. CPU, Memory, Process I/O, and GPU should remain available when the broker is stopped.

If the client reports `Broker Unavailable` while connecting, collect the per-user/session pipe name and identity diagnostics. The expected owner is LocalSystem (`S-1-5-18`); the DACL must contain LocalSystem, the configured user/logon SID, and the dedicated service SID, and must deny Network SID. A connect-time `UnauthorizedAccessException` means the client ACE did not match the installed user/session; it is not an ETW provider authorization result. After a successful v1 handshake, ETW failures are reported separately with the native operation and Win32 status.

Current development builds distinguish `Broker service not installed`,
`Broker service stopped`, `Broker connection failed`, protocol mismatch,
`ETW unavailable`, and `No attributed activity yet`. The last state means the
broker is healthy and the application has not accumulated attributed bytes; a
measured zero remains a real zero.

From the repository root, use the tracked development/operator commands:

```powershell
# Install/start: elevated PowerShell/UAC required
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\privileged-broker\Manage-PrivilegedBroker.ps1" -Mode Install

# Status: normal PowerShell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\privileged-broker\Manage-PrivilegedBroker.ps1" -Mode Status

# Remove: elevated PowerShell/UAC required
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\privileged-broker\Manage-PrivilegedBroker.ps1" -Mode Remove
```

The app itself remains `asInvoker`; running it as Administrator does not install
or replace the Broker. When the service is absent or stopped, Network and
Physical Disk remain honestly `Unavailable`.
