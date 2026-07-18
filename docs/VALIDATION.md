# Validation log

## 2026-07-18 foundation and first vertical slice

Historical environment state from the initial scaffold; the SDK was installed later the same day as recorded below.

Environment inspection:

- Windows x64 with Visual Studio Community 2026 18.8.0.
- .NET runtimes 6.0.5 and 8.0.11 were present.
- No .NET SDK, WinUI workload, embedded Visual Studio SDK, or Windows SDK was discoverable.
- Drive C had approximately 48 MB free before cleanup, which is insufficient for the .NET 10 SDK and NuGet packages.

Commands attempted:

```powershell
dotnet --info
dotnet workload list
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget --accept-package-agreements --accept-source-agreements --silent --disable-interactivity
powershell -ExecutionPolicy Bypass -File .tools/dotnet-install.ps1 -Version 10.0.302 -InstallDir .tools/dotnet -Architecture x64 -NoPath
MSBuild.exe MonitoringXS.sln /t:Restore /p:Configuration=Release /p:Platform=x64
```

Actual results:

- `dotnet --info`: failed to find any SDK.
- WinGet identified official SDK 10.0.302 but failed in its temporary cache with access denied.
- The workspace-local official installer failed with `There is not enough space on the disk`.
- Visual Studio MSBuild reported `MSB4057: The target "Restore" does not exist` for SDK-style projects because the .NET SDK targets are absent.
- XML parsing succeeded for all 20 project, props, manifest, and XAML files.
- Every `ProjectReference` resolved to an existing project file.
- C# compilation, XAML compilation, test execution, and application launch were not completed and are not claimed.

Only two temporary files created by the failed installers were deleted (0 bytes and approximately 13 MB). No user project or personal files were removed.

## 2026-07-18 continuation and process-I/O slice

Environment changes:

- .NET SDK 10.0.302 and .NET runtime 10.0.10 are now installed.
- No optional .NET workloads are installed.
- NuGet connectivity remains unreliable. A sandboxed restore failed TLS authentication; two approved restores outside the sandbox timed out after 184 and 244 seconds.
- A solution build reached the WinUI project but failed when `Microsoft.WindowsAppSDK.Runtime` 2.2.0 returned an unexpected EOF. This is the only project that did not build.

Corrections and implementation validated:

- Replaced unsupported source-generated Toolhelp P/Invoke signatures with compatible `DllImport` declarations; `MonitoringXS.Platform.Windows` now compiles.
- Added real `GetProcessIoCounters` sampling behind `IProcessIoCounterReader`, including process-start verification to reject PID reuse.
- Added one-second process I/O read/write rates, cumulative byte/operation counts, application aggregation, and partial/lower-bound metric semantics.
- Kept these metrics labelled `Process I/O`: the Windows API reports all process I/O and does not prove physical-disk-only activity.
- Updated the card/detail projections for the new metrics. XAML compilation and visual review are not claimed because the WinUI runtime package is unavailable.

Commands and actual results:

```powershell
dotnet build src/MonitoringXS.Core/MonitoringXS.Core.csproj -c Release --no-restore
dotnet build src/MonitoringXS.Application/MonitoringXS.Application.csproj -c Release --no-restore
dotnet build src/MonitoringXS.Collectors/MonitoringXS.Collectors.csproj -c Release --no-restore
dotnet build src/MonitoringXS.Platform.Windows/MonitoringXS.Platform.Windows.csproj -c Release --no-restore
dotnet build src/MonitoringXS.Storage/MonitoringXS.Storage.csproj -c Release --no-restore
dotnet build src/MonitoringXS.DesignSystem/MonitoringXS.DesignSystem.csproj -c Release --no-restore
dotnet build src/MonitoringXS.ElevatedHelper/MonitoringXS.ElevatedHelper.csproj -c Release --no-restore
dotnet build benchmarks/MonitoringXS.Benchmarks/MonitoringXS.Benchmarks.csproj -c Release --no-restore
```

All listed projects succeeded with 0 warnings and 0 errors.

```powershell
dotnet test MonitoringXS.sln -c Release --no-build
```

Succeeded: 20 passed, 0 failed, 0 skipped across Core (1), Application (1), Collectors (3), Integration (14), and Storage (1).

```powershell
dotnet build MonitoringXS.sln -c Release --no-restore
```

Failed only for `MonitoringXS.App`: the feed listed `Microsoft.WindowsAppSDK.Runtime` 2.2.0 but repeated package downloads ended with an unexpected EOF. All other solution projects built in the same invocation.

## Required next validation

Restore the Windows App SDK packages on a stable NuGet connection and ensure the Visual Studio **WinUI application development** workload is available, then run:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

Do not mark the vertical slice validated until these commands pass and the UI has been launched and visually/keyboard inspected.
