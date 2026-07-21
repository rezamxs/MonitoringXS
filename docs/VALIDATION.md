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

## 2026-07-18 WinUI restore and launch confirmation

The previously blocked Windows App SDK restore completed after the package connection recovered. The WinUI application was corrected to merge `XamlControlsResources` and to use a concrete x64 architecture for its self-contained Windows App SDK build.

Commands and actual results:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

- Restore succeeded for the full solution.
- Release build succeeded for the full solution, including XAML compilation for `MonitoringXS.App`.
- Tests succeeded: 20 passed, 0 failed, 0 skipped.
- The x64 WinUI application launched successfully.

This confirms the toolchain, dependency restore, XAML compilation, and application startup. A focused runtime, visual, keyboard, and accessibility smoke test is still required before declaring the vertical slice stable.

## 2026-07-19 confirmed full-solution baseline

Before this continuation, the full validation path was confirmed successful:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

- Restore succeeded for the complete solution.
- The Release build succeeded for the complete solution, including the WinUI project.
- All automated tests passed.
- The x64 WinUI application launched successfully.

These are the confirmed incoming baseline results. The focused runtime and visual smoke test, Milestone 1 implementation, and final validation performed during this continuation are recorded separately below when completed.

## 2026-07-19 Milestone 1 implementation and smoke validation

Implemented and exercised:

- Installed Win32 uninstall-registry catalog and current-user MSIX app-list catalog, both bounded and time-cached (including empty results).
- Process package family/full-name/AppUserModelID mapping with a bounded PID-plus-start-time cache.
- Bounded executable metadata, embedded Authenticode certificate, and icon extraction caches.
- Validated, capacity-bounded, atomically persisted user attribution overrides.
- Catalog-backed installed/packaged/portable decisions with explicit confidence and human-readable reasons.
- Native process/window snapshots, PID-reuse verification, bounded live process/metadata caches, and single-handle resource sampling.

Focused runtime, visual, keyboard, accessibility, and performance smoke results on the x64 Release application:

- The application populated real application cards and updated the status line (`1 installed · 3 portable` in the final smoke environment; counts varied with running applications).
- A card received keyboard focus and Enter opened a second logical-application tab.
- Section labels were not keyboard-focusable; the application cards, navigation, toggle, tab, close button, chart group, and expander exposed Automation names/roles.
- CPU and process-I/O values left `Warming up` after the second sample; the detail expander displayed classification confidence and evidence.
- The window remained responsive. Working set was 154.9 MB.
- On Windows 10 build 19045.6466 with an 8-logical-processor Intel Core i7-2630QM, the following 30-second steady phase with a live application tab open used 0.573% of total CPU capacity. See `PERFORMANCE.md` for scope and the post-start phase.
- Visual inspection found no clipping at the configured 1180 × 760 window size, binding failure text, unavailable-data fabrication, or navigation/tab mismatch.

The successful smoke launch occurred after all Milestone 1 feature and performance changes. A final shutdown-only lifecycle correction (defer token-source disposal until the monitoring loop exits) was applied afterward and was covered by the final build and test run below.

## 2026-07-19 final command results

The first sandboxed invocation of the standard restore command failed because the sandbox NuGet client could not authenticate TLS to `https://api.nuget.org/v3/index.json`. The approved outside-sandbox retry was rejected by the tool service's usage limit rather than by NuGet or the repository. The sandbox's default global-package path was then found to differ from the populated user cache.

The final successful restore used the existing populated user package cache and performed no dependency changes:

```powershell
$env:NUGET_PACKAGES="$env:USERPROFILE\.nuget\packages"
dotnet restore MonitoringXS.sln
```

Result: succeeded for all 14 projects with 0 errors. Ten `NU1900` warnings reported that the online vulnerability-data feed was unreachable; package resolution itself succeeded.

```powershell
dotnet build MonitoringXS.sln -c Release --no-restore
```

Result: succeeded for the full solution, including WinUI XAML compilation, with 0 errors and the same 10 `NU1900` vulnerability-feed warnings. There were no compiler, analyzer, or XAML warnings.

```powershell
dotnet test MonitoringXS.sln -c Release --no-build
```

Result: 44 passed, 0 failed, 0 skipped:

- Core: 1 passed.
- Application: 3 passed.
- Collectors: 3 passed.
- Integration: 33 passed.
- Storage: 4 passed.

A requested post-build desktop launch was not executed because the external approval service rejected it after reaching its usage limit. It is not claimed as executed. The functional Milestone 1 and performance code had already completed the successful launch and focused smoke sequence above; the later shutdown-only correction compiled successfully and all tests passed, but that exact final binary was not launched by the tool.

## 2026-07-21 repository-cleanup validation

Validation was performed after removing only ignored, reproducible build intermediates and outputs. The global NuGet package cache, project inputs, local application data, SDKs, and runtimes were not removed.

Commands executed:

```powershell
git status
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

Actual results:

- `git status` completed successfully and continued to show the pre-existing modified and untracked source/documentation work; generated cleanup targets were ignored and did not appear as source deletions.
- The first sandboxed restore failed with `NU1301` because TLS authentication to NuGet was unavailable. The same restore command was retried with network access and succeeded for all 14 projects.
- The first sandboxed build failed during its implicit restore with the same `NU1301` network restriction. The same build command was retried with network access and succeeded for the complete Release solution with 0 warnings and 0 errors in 00:02:51.83.
- The first sandboxed test command likewise stopped during its implicit restore. Its network-enabled retry succeeded: 44 passed, 0 failed, and 0 skipped across Core (1), Application (3), Collectors (3), Integration (33), and Storage (4).
- The Debug launch command started `MonitoringXS.App.exe`. A live-window probe observed a visible top-level window titled `Monitoring XS` with a nonzero native window handle (`0x30C0592`). This confirms process startup and main-window creation only; no additional visual, keyboard, or accessibility smoke result is claimed for this launch.

## 2026-07-21 physical-disk ETW implementation and validation

Compatibility was checked before changing the production package graph. An ignored two-project probe targeted `net10.0-windows10.0.17763.0` for the TraceEvent platform assembly and `net10.0-windows10.0.26100.0` with Windows App SDK 2.2.0 for the app-facing assembly. It used central package management and Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5. Restore and Release build succeeded with 0 warnings and 0 errors. The final Platform package graph resolves TraceEvent exactly to 3.2.5; no unrelated direct package version changed and the global NuGet cache was not cleared.

The incoming baseline was also executed before implementation:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
```

- Restore succeeded for all 14 projects after the network-enabled retry.
- Release build succeeded with 0 warnings and 0 errors in 00:02:30.99.
- Tests succeeded: 44 passed, 0 failed, 0 skipped.

Implemented and covered:

- A single lazy real-time kernel ETW session using disk-I/O and thread keywords only, with `NoRestartOnCreate` conflict safety and no automatic elevation.
- A bounded 16,384-event queue and bounded 32,768-entry thread-to-PID map.
- Immediate conversion of ETW wall-clock timestamps to UTC. Raw QPC-relative timestamps do not enter Core and are never compared with `ProcessInstanceId.StartTimeUtc`.
- PID-plus-UTC-start-time reuse rejection, read/write rates, operation counts, session totals, healthy zero after warm-up, and explicit `Available`, `Partial`, `WarmingUp`, `Unavailable`, `AccessDenied`, and `Error` states.
- Current-batch and thread-map discard after ETW loss to prevent stale-thread PID attribution.
- Separate Process I/O and Physical disk (ETW) presentation, including lower-bound and collector diagnostics.

Final commands and actual results:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

- Restore succeeded; all projects were up-to-date.
- Release build succeeded for the complete solution, including WinUI XAML compilation, with 0 warnings and 0 errors in 00:01:54.26.
- Tests succeeded: 58 passed, 0 failed, 0 skipped: Core 3, Application 4, Collectors 12, Integration 35, Storage 4.
- The Debug command started `MonitoringXS.App.exe` (PID 14632). The process was responsive with a visible top-level window titled `Monitoring XS` and nonzero native handle 17892958. Working set at the observation point was 152,891,392 bytes.
- UI Automation observed a Google Chrome application tab, separate `Physical disk (ETW)` read/write groups, and the honest non-elevated status `Access denied`. Chrome and VS Code processes were present at smoke start, but only Chrome attribution was observed in the UI; VS Code UI attribution is not claimed.
- No UAC prompt or administrator launch was attempted. Consequently, an `Available` live ETW value was not observed and is not claimed.
- Closing used the window's normal close request. It returned true, the app exited within 10 seconds, and the parent `dotnet run` command returned exit code 0.

Measured harness results (smoke figures, not release performance claims):

```text
Synthetic aggregation: 200 processes, 10,000 events, 44.41 ms,
20,480,000 attributed read bytes and 20,480,000 attributed write bytes.

Disk workload: 16,777,216 bytes written with WriteThrough in 73.31 ms,
then 16,777,216 bytes read in 16.70 ms.
```

After explicit approval, the ignored compatibility-probe directory and the original 16 MiB workload artifact were removed. The exact cleanup result is recorded below. No Git commit, package-cache deletion, SDK/workload change, or automatic elevation was performed.

## 2026-07-21 approved artifact cleanup

Before deletion, `git status` was run and `git ls-files` confirmed that neither target contained a tracked path. Only these pre-inventoried reproducible targets were removed:

- `.artifacts\TraceEventCompatibilityProbe`: 66,219,380 bytes.
- `.artifacts\DiskSmoke\monitoringxs-disk-smoke-6a351e2bb7974294a990e6d16ab527b3.bin`: 16,777,216 bytes.

Exact space freed: 82,996,596 bytes. Both targets were absent afterward, post-cleanup `git status` reported no tracked deletion, and no tracked file was modified by the cleanup. The global NuGet cache and all paths outside these two explicit targets were untouched.

## 2026-07-21 elevated physical-disk runtime smoke

The application was launched from an Administrator PowerShell session with:

```powershell
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Release --no-build
```

The controlled 15-second `MonitoringXS.Benchmarks` workload was visible as one logical application. Actual UI observations from its detail tab were:

- collector status `Available`;
- physical read samples of 42.2 and 44.4 KB/s;
- physical write samples of 189.7 and 298.4 KB/s;
- separate `Process I/O` and `Physical disk (ETW)` labels;
- diagnostics reaching 198 events/s and a maximum queue depth of 895, with 0 queue drops, 0 ETW-lost events, a configured 32 MB session buffer budget, 0 unattributed events, and 0 PID-reuse rejections;
- `logman` reported `Buffer Size: 64`, 0 buffers lost, and 7 buffers written during this instrumented pass;
- Microsoft Edge reached an independent 62.4 KB/s physical-read sample and VS Code remained at 0 B/s. Neither received a workload-scale spike; the workload's nonzero physical rates remained on the `MonitoringXS.Benchmarks` logical application;
- the UI displayed a `Partial` aggregate with `>=` lower-bound values during observed incomplete data, then returned to live/available values.

No PID reuse occurred during the manual interval (`PID-reuse rejected 0`), so runtime reuse rejection is not claimed. Deterministic UTC-domain tests exercise rejection of an event older than a reused PID's `StartTimeUtc`; raw QPC values are never compared with UTC.

The UI-Automation-heavy observation pass ended in a WinUI/XAML fail-fast (`0x88000fa8`) before the controller requested close, and its ETW session had to be stopped explicitly. That pass is not used to claim responsive UI or clean shutdown. A second, low-intrusion elevated lifecycle pass ran the controlled workload without list scrolling/click automation and observed:

- the `Monitoring XS` main window and an active ETW session;
- a responsive process at every sample;
- workload exit code 0;
- 2.252% of total machine CPU capacity during the active workload interval, maximum working set 163,106,816 bytes, and maximum handle count 911;
- a successful normal window-close request, clean application exit, and `dotnet run` exit code 0;
- no `MonitoringXS.PhysicalDisk.v1` session after exit (`Data Collector Set was not found`); `logman` reported 0 buffers lost and 6 buffers written during this lifecycle pass.

The elevated smoke was manual and one-time. It did not change the manifest, add automatic startup elevation, install a service/driver/helper, or alter normal unelevated `Access denied` degradation.

Post-smoke full-solution validation used the requested commands. Each first sandboxed invocation stopped only because sandbox TLS authentication could not reach NuGet (`NU1301`). The same command was then rerun with normal network access; no package version was changed and the global package cache was not cleared.

```powershell
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
```

- Release build succeeded for the complete solution with 0 warnings and 0 errors in 00:02:25.73.
- Tests succeeded: 60 passed, 0 failed, 0 skipped: Core 3, Application 4, Collectors 14, Integration 35, Storage 4.
