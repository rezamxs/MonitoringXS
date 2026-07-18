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

## Corrupt local history

Use the future in-app recovery action to quarantine and recreate the database. Do not manually overwrite a database while Monitoring XS is running.
