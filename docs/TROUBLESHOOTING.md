# Troubleshooting

## `No .NET SDKs were found`

Install the .NET 10 SDK (the runtime alone is insufficient) and restart the terminal. Verify with `dotnet --list-sdks`.

## WinUI targets or templates are missing

In Visual Studio Installer add the stable **WinUI application development** workload and a current Windows SDK. Do not select preview Windows App SDK packages for production.

## Restore cannot reach NuGet

Confirm `https://api.nuget.org/v3/index.json` is reachable and that an authenticated proxy is configured in NuGet. Do not commit machine credentials.

## Counters are unavailable

Monitoring XS must keep other metrics running and show `Unavailable`. Confirm the Windows performance counter service/ETW permissions and graphics driver are healthy. Never substitute zero.

## Access denied for a process

Protected or higher-integrity processes may expose limited metadata. This is expected degradation; the primary app should remain unelevated.

## Physical disk (ETW) is unavailable

Kernel ETW can require administrator rights or membership in **Performance Log Users**. Monitoring XS does not open UAC automatically: it shows `Access denied` while CPU, memory, and Process I/O continue. For a deliberate manual diagnostic, start PowerShell as Administrator and launch the app from that shell.

The kernel provider must be enabled before accessing the TraceEvent source/parser; current builds enforce this ordering. If an older build reports `The kernel provider must be enabled first and only once in a session`, rebuild before retrying.

Check a suspected stale session from an Administrator shell with `logman query MonitoringXS.PhysicalDisk.v1 -ets`. The app deliberately will not replace an existing same-name session. Stop it only after confirming that no Monitoring XS process owns it. ETW loss or local queue overflow is shown as `Partial` because displayed mapped values are lower bounds; unattributed events remain visible in diagnostics without contaminating mapped applications.

## Attribution overrides are unavailable

Monitoring XS reports an invalid, inaccessible, or unsupported `%LocalAppData%\MonitoringXS\attribution-overrides.json` file as unavailable and continues without fabricating mappings. Preserve the file for diagnosis or move it aside while the app is closed; the app will create a new versioned document after the next explicit override change.

## Corrupt local history

Use the future in-app recovery action to quarantine and recreate the database. Do not manually overwrite a database while Monitoring XS is running.
