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

## 2026-07-21 WinUI layout-cycle reliability investigation

The previously observed UI-Automation fail-fast was reproduced before changing the product. The equivalent elevated workload scenario produced the same `0xc000027b` exception, `Microsoft.UI.Xaml.dll` offset `0x3ad79d`, and WER bucket as the original failure. A smaller normal, unelevated scenario then reproduced it by opening one application tab and expanding `Advanced application information`; opening the tab without expanding remained stable. Expanding with the keyboard Space key also crashed, which ruled out an AutomationPeer-only recursion or test-tool-only failure.

The archived WER directories contained seven historical `Report.wer` files but no retained minidumps; the `.mdmp` files referenced by Event Viewer had already been removed from WER's temporary directory. A temporary diagnostic `Application.UnhandledException` observer was used without setting `Handled`. It captured the actual exception before fail-fast:

```text
Microsoft.UI.Xaml.LayoutCycleException
Layout cycle detected. Layout could not complete.
```

The root cause was synchronous `MetricSparkline.Redraw()` from `ChartRoot.SizeChanged`. Expanding the adjacent advanced section changed layout; redraw immediately changed the polyline and summary text during that same layout pass, creating a second layout invalidation cycle. Removing only the `SizeChanged` callback made the failing keyboard-plus-Expander scenario stable, confirming causality. The final fix preserves resize redraws but coalesces them and dispatches one redraw at low priority after the active layout pass. The temporary exception observer was removed.

Evidence against the other investigated causes:

- Observable collections are mutated on the captured UI context, and stale-item removal enumerates a snapshot; no cross-thread UI access or collection-enumeration exception was observed.
- The deterministic crash occurred before shutdown, and normal close remained clean, excluding dispatcher teardown and shutdown disposal as its trigger.
- Opening the tab without expanding was stable, and XAML compilation succeeded, excluding a generally invalid data template.
- Keyboard expansion reproduced the crash without `ExpandCollapsePattern`, excluding AutomationPeer recursion as the root trigger.
- Enumeration plus 260 list scroll operations completed without error before the advanced section was involved, excluding automation pressure by itself.

A reusable real-UI regression harness was added at `scripts/validation/Invoke-MonitoringXsUiAutomationStress.ps1`. It launches the actual Release app, opens a logical application tab by keyboard, toggles the advanced Expander, resizes the window, enumerates the UI Automation tree, checks responsiveness and Event Viewer, and requires a normal clean exit.

Final commands and results:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validation\Invoke-MonitoringXsUiAutomationStress.ps1 -Configuration Release -DurationSeconds 60
```

- The first sandboxed restore failed only with NuGet TLS `NU1301`; the normal-network retry restored all projects successfully without clearing the global package cache.
- The full Release build succeeded with 0 warnings and 0 errors in 00:02:22.91.
- All 60 tests passed: Core 3, Application 4, Collectors 14, Integration 35, Storage 4; 0 failed and 0 skipped.
- A normal 20-second WinUI smoke observed the `Monitoring XS` main window, responsiveness at every sample, a successful normal close, `dotnet run` exit code 0, and no new Application Error event.
- The final 60-second automation stress opened the tab by keyboard and the Expander, read 14,420 automation elements, performed 29 Expander toggles and 55 window resizes, reported 0 automation errors and 0 crash events, remained responsive at every sample, and exited cleanly with code 0.
- After a 30-second warm-up, the 60.739-second idle interval used 0.637% of total eight-logical-processor capacity. Working set was 149,823,488 bytes minimum, 152,630,524 bytes average, 158,068,736 bytes maximum, and 153,739,264 bytes at the final sample.

Before deletion, all ten files in `.artifacts\ElevatedSmoke` were confirmed ignored and untracked. Those reproducible files, totaling 25,198,196 bytes, were deleted; the directory is empty. `.gitignore` already excludes `.artifacts/`.

## 2026-07-21 Milestone 3A network implementation and partial runtime validation

The implementation uses the existing fixed kernel ETW session with the `NetworkTCPIP` keyword. Typed IPv4/IPv6 TCP and UDP send/receive events provide PID and byte count. ETW timestamps are converted to UTC before comparison with `ProcessInstanceId.StartTimeUtc`. Network events have a separate bounded 16,384-event queue; event loss and overflow use persistent lower-bound session semantics. Current TCP/UDP counts use bounded IP Helper owner-PID table snapshots without retaining addresses or ports.

No dependency was added or updated. `Microsoft.Diagnostics.Tracing.TraceEvent` remains centrally pinned at 3.2.5, and the global NuGet cache was not cleared.

Commands executed:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Release --no-build
```

Actual results:

- The first sandboxed restore failed only because NuGet signature metadata returned TLS `NU1301`. The approved normal-network retry restored all 14 projects successfully.
- The full Release build succeeded with 0 warnings and 0 errors in 00:02:21.48.
- All 83 tests passed with 0 failures and 0 skipped: Core 4, Application 5, Collectors 35, Integration 35, and Storage 4.
- Deterministic network coverage includes download/upload aggregation, rates, retained-session totals, logical-application separation, UTC PID-reuse rejection, process exit, warming up, healthy zero, unavailable/partial states, ETW loss, queue overflow, access denied, unsupported, session conflict, cancellation, queue diagnostics, and endpoint snapshots.
- `MonitoringXS.App.exe` started as PID 15724. A visible responsive main window titled `Monitoring XS` with handle 25167104 was observed. UI Automation read cards for Google Chrome, Visual Studio Code, and Monitoring XS and found the separate Network field.
- The normal unelevated collector status was `Access denied` for all observed cards. No network rate, controlled-browser attribution, event rate, queue depth, dropped/lost count, or unattributed-event result is claimed because the kernel session did not start. Process I/O and Physical disk remained separate fields.
- After a 30.517-second warm-up, a 60.675-second steady interval used 0.444% of total eight-logical-processor capacity. Working set was 151,945,216 bytes minimum, 154,113,229 bytes average, 155,783,168 bytes maximum, and 151,961,600 bytes final. The process was responsive for all 60 samples.
- A normal close request issued through the restricted process API returned `False`; the app and `dotnet run` processes therefore remained alive and clean shutdown is not claimed. `logman query MonitoringXS.PhysicalDisk.v1 -ets` reported `Data Collector Set was not found`, so no ETW session was left active.

Milestone 3A remains partial until a permitted runtime can observe real browser traffic attribution and a clean normal shutdown. No elevated retry, automatic UAC request, GPU work, history, actions, packaging, commit, or push was performed.

## 2026-07-22 Milestone 3A accessibility and session stabilization

The existing uncommitted Milestone 3A worktree was preserved on `feature/m3a-network-stabilization`. Before editing it contained 18 modified tracked files and 15 untracked files; the tracked diff was 516 insertions and 26 deletions.

The stale application-card UI Automation name was reproduced before the fix: the `ListViewItem` continued to announce `CPU Warming up` and an old memory value while its visible text had already changed to live values. The container name had been assigned once from `ContainerContentChanging`, so later `ApplicationCardViewModel.AutomationName` notifications had no binding target. The fix binds `AutomationProperties.Name` one-way to the observable view-model property and keeps the existing keyboard/focus behavior. The accessible text now includes the application and running state, CPU, memory, Process I/O, Physical disk state, and Network state without publisher or diagnostic detail.

The shared disk/network kernel session was renamed from the disk-specific `MonitoringXS.PhysicalDisk.v1` to the neutral versioned `MonitoringXS.KernelMetrics.v1`. Network production events still come from the kernel `NetworkTCPIP` provider; no random, fake, or placeholder production values were added.

Commands executed:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

Actual results:

- Restore succeeded with all projects up to date. The global NuGet cache was not cleared.
- The full Release build succeeded with 0 warnings and 0 errors in 00:00:28.70.
- All 85 tests passed with 0 failures and 0 skipped: Core 4, Application 5, Collectors 35, Integration 36, Storage 4, and App 1.
- The new deterministic App regression test first failed against the old accessible text because Process I/O was absent. It then passed after the binding/text fix. A second integration test protects the neutral versioned session name.
- `MonitoringXS.App.exe` started as PID 10232. A visible main window titled `Monitoring XS` with native handle 17499984 was observed.
- Six application-card accessible names changed from `metrics warming up` to live CPU, memory, and Process I/O values. Chrome reached CPU 2.8%, memory 3.15 GB, Process I/O 868.7 KB/s read and 222.7 KB/s write during the observation. These are process-counter values, not Network or Physical disk values.
- Each updated accessible name exposed separate Process I/O, Physical disk, and Network states. The unelevated kernel session returned `Access denied` for Physical disk and Network; no zero or fabricated kernel metric was displayed.
- Keyboard Enter opened an application tab. The UI was responsive at every sample, no new Application Error event was recorded, normal close succeeded, and `dotnet run` exited with code 0.
- After exit, `logman query MonitoringXS.KernelMetrics.v1 -ets` and a compatibility check for the old `MonitoringXS.PhysicalDisk.v1` name both returned `Data Collector Set was not found`.

No elevated run was performed. Real ETW Network rates, controlled browser traffic attribution, event rate, queue depth, dropped events, ETW loss, and endpoint counts could not be observed because this unelevated machine returned `Access denied`. Milestone 3A therefore remains incomplete; `MILESTONES.md` was not advanced.

## 2026-07-22 Milestone 3A elevated network runtime validation

The final runtime pass was launched from an Administrator PowerShell session. Before launch, both `MonitoringXS.KernelMetrics.v1` and the retired `MonitoringXS.PhysicalDisk.v1` session name were absent. No automatic elevation, service, driver, helper, package change, or global NuGet-cache cleanup was used.

Commands executed:

```powershell
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release --no-build
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

Build and test results:

- The final Release build succeeded with 0 warnings and 0 errors in 00:00:14.40.
- All 85 tests passed with 0 failures and 0 skipped: Core 4, Application 5, Collectors 35, Integration 36, Storage 4, and App 1.

The controlled browser workload used Google Chrome with Cloudflare's documented speed-test endpoints. It requested a 100,000,000-byte download from `https://speed.cloudflare.com/__down` and posted a 26,214,400-byte form body to `https://speed.cloudflare.com/__up`. Monitoring XS observed the Chrome session total move from 2.6 MB downloaded / 135.0 KB uploaded to 54.5 MB downloaded / 8.9 MB uploaded during the recorded interval. These are the values actually observed by the application; the requested transfer sizes are not reported as completed transfer totals.

Actual UI and attribution observations:

- Physical disk and Network were both live numeric metrics rather than `Access denied`. The Network status was `Available; reason None.` Elevation was required on this machine because the earlier unelevated pass returned `Access denied`.
- Google Chrome reached 1.6 MB/s download and 3.6 MB/s upload. Both rates were displayed on the Google Chrome logical application.
- The largest unrelated download was Telegram Desktop at 10.4 KB/s. The largest unrelated upload was Visual Studio Code at 6.8 KB/s. Neither received a workload-scale spike comparable with Chrome.
- At the peak Chrome upload sample, the card separately showed Process I/O at 1.6 MB/s read / 1.6 MB/s write, Physical disk at 1.8 MB/s read / 97.1 KB/s write, and Network at 983 B/s down / 3.6 MB/s up. At the peak download sample, Network was 1.6 MB/s down / 1.3 KB/s up while the other two metric groups remained separately labeled.
- The UI remained responsive for all 85 card samples. UI Automation recorded 0 errors while switching between the running-app list and Chrome detail tab.

Maximum observed Network diagnostics were:

- event rate: 1,005 events/s;
- current queue depth: 1,055;
- maximum queue depth: 1,762 of the bounded 16,384-event queue;
- queue-dropped events: 0;
- ETW-lost events: 0;
- unattributed events: 0;
- PID-reuse rejected events: 0;
- endpoint counts: 20 TCP connections and 12 UDP endpoints;
- completeness: complete, with no lower-bound interval in this pass.

While the application was running, `logman` reported `MonitoringXS.KernelMetrics.v1` as active with a 64 KB buffer size, 0 buffers lost, and 46 buffers written. A separate elevated lifecycle pass used native `GetExitCodeProcess` observation and confirmed a normal close request, `MonitoringXS.App.exe` exit code 0, `dotnet run` exit code 0, no remaining `MonitoringXS.App` process, and absence of both the current and retired ETW session names after exit.

No actual OS PID reuse occurred during this pass (`PID-reuse rejected 0`). PID/start-time protection remains covered by deterministic UTC-domain tests. This runtime result is from one development machine and does not replace validation on other Windows versions, hardware, network conditions, or security policies.

Milestone 3A acceptance criteria are satisfied on the recorded development machine. GPU work was not started.

## 2026-07-22 Milestone 4 sorting, title bar, and card hierarchy validation

This focused UI pass did not change collectors, attribution, metric semantics, GPU, history, application actions, or packaging. Sorting is applied separately inside the installed and portable sections. Available and partial measured values use their real numeric value; warming-up, denied, unsupported, and other unavailable states remain after measured values in both directions. Equal values use application name as the stable secondary key. Live metric reordering is limited to a five-second interval, while user and membership changes apply immediately.

Commands executed:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug --no-restore
```

Actual build and test results:

- The sandboxed restore failed with NuGet TLS/credential `NU1301`. The same command succeeded with normal network access and reported all projects up to date. No package version changed and the global NuGet cache was not cleared.
- The final Release build succeeded with 0 warnings and 0 errors in 00:01:23.73.
- All 96 tests passed with 0 failures and 0 skipped: Core 4, Application 5, Collectors 35, Integration 36, Storage 4, and App 12.
- New deterministic App tests cover all seven sort fields, both name directions, measured-before-unavailable behavior in both metric directions, application-name tie breaking, partial lower-bound values, combined read/write or download/upload rates, card identity preservation, and the five-second anti-jitter policy including forced refresh and clock rollback.

Actual WinUI and UI Automation observations on Windows 10 build 19045 at 96 DPI:

- A visible responsive `Monitoring XS` window opened. UI Automation exposed the native Minimize, Maximize, and Close buttons plus a `Monitoring XS` title-bar element.
- All seven fields were selected in ascending and descending directions: Application name, CPU usage, Memory usage, Process I/O rate, Physical Disk rate, Network rate, and Process count. Installed and portable applications remained in separate sections.
- Physical Disk and Network both honestly showed `Access denied` in this unelevated pass. Because every observed card was unavailable for those two fields, both directions used application name as their stable secondary order; no unavailable value was treated as zero.
- Chrome selection was `True` before cycling through all sort fields and remained `True` afterward. During a separate 12-second CPU-sort observation, Chrome remained selected and keyboard-focused in every sample.
- Chrome's accessible card name changed as live CPU and memory changed. CPU values observed during that interval ranged from 12.6% to 23.8%, and working set moved from 2.36 GB to 2.38 GB. The card order had one distinct value during the interval, with no visible one-second reorder jitter.
- Keyboard Enter on the focused Chrome card opened `Google Chrome application tab`. That tab remained open after returning to Running Apps and changing the sort field.
- Double-clicking the custom drag region changed the window from its 1180 x 760 restored bounds to maximized and a second double-click restored it. A drag moved the restored bounds from `(52, 52)` to `(114, 88)`. The native Minimize button minimized the window and Windows restored it normally.
- The light theme was visually inspected at 1366 x 768 and 100% scaling. Metric text wrapped at the available width, selection remained visible, and no decorative animation or shadow was added. The final screenshot is `.artifacts/ui-polish-after.png`; it is ignored by Git.
- Dark theme, High Contrast, 150-200% scaling, Windows 11 Snap Layout, and a broader screen-reader pass were not executed on this Windows 10/96-DPI environment and remain open Milestone 4 validation work.
- A normal close request returned `True`; `MonitoringXS.App` and its `dotnet run` parent both exited, and no process remained. This shell did not retain a numeric exit code, so exit code zero is not claimed for this pass.
- After exit, both `MonitoringXS.KernelMetrics.v1` and the retired `MonitoringXS.PhysicalDisk.v1` query returned `Data Collector Set was not found`.

The UI refinement is validated for the observed Windows 10 light-theme environment. Milestone 4 remains in progress because the other product pages and the unexecuted theme, scaling, Snap Layout, and screen-reader checks are still required.

## 2026-07-23 two-tier application-card validation

This focused visual correction changed only the Running Apps presentation and its view-model formatting. Collectors, attribution, metric models, package versions, project configuration, and unrelated pages were not changed.

Commands executed:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
dotnet test MonitoringXS.sln -c Release --no-build -m:1
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

Actual build and test results:

- The sandboxed restore failed with NuGet repository-signature TLS/credential `NU1301`. The same restore succeeded with normal network access and reported every project up to date. The global NuGet cache was not cleared.
- The initial Release build succeeded in 00:01:15.76 with 0 warnings and 0 errors. The final Release build after the tests succeeded in 00:00:39.59 with the same clean result.
- The first solution-wide parallel test run passed Core 4, Application 5, Collectors 35, Storage 4, and Integration 36, but the xUnit runner failed while discovering the WinUI App assembly after reporting that its test process did not return valid JSON. The App assembly then passed independently with 17 tests. A final sequential solution run with `-m:1` passed all 101 tests with 0 failures and 0 skipped: Core 4, Application 5, Collectors 35, Storage 4, Integration 36, and App 17.
- New deterministic App tests verify that matching denied, warming-up, unavailable, and unsupported directions collapse to one visible state, partial pairs keep their measured lower-bound values, and the card's accessible text follows metric changes.

Actual WinUI and UI Automation observations on Windows 10 build 19045 at 96 DPI:

- A visible responsive `Monitoring XS` window opened at 1180 x 760. UI Automation exposed three application cards and the native Minimize, Maximize, and Close buttons.
- The first card's accessible name changed from CPU 8.9% and memory 2.10 GB to CPU 7.7% and memory 2.09 GB after three seconds. It continued to include application identity, running state, Process I/O, Physical disk state, and Network state. No `Unavailable read` or `Unavailable write` duplication was exposed.
- CPU and memory were visually primary. Process I/O, Physical disk, and Network stayed in a separate secondary tier. Physical disk and Network honestly displayed `Unavailable` with the supporting reason `Access denied`; neither was displayed as zero.
- Advanced mode changed from Off to On and returned to Off through its UI Automation Toggle pattern. The sort-direction control changed from ascending to descending and updated its accessible action name.
- Keyboard focus and Enter on the Visual Studio Code card opened `Visual Studio Code application tab`. The UI remained responsive.
- The shared content column remained fluid at its normal width. Additional width-stress observations at 787 x 760 and 590 x 760 showed no horizontal scrollbar; long identity text ellipsized and secondary values wrapped. These are width proxies, not claims of actual 150% or 200% DPI validation.
- A normal close request succeeded and `MonitoringXS.App` exited without a remaining application process. The surrounding command wrapper timed out, so a numeric `dotnet run` exit code is not claimed.
- The final light-theme screenshot is `.artifacts/ui-two-tier-final-100.png`; it is ignored by Git.

Actual 150% and 200% display scaling, Dark theme, High Contrast, Windows 11 Snap Layout, and a broader screen-reader pass were not executed because this machine remained at 96 DPI in its current light-theme user session. Milestone 4 therefore remains in progress.

## 2026-07-23 title-bar, live-chart, and appearance validation

The incoming worktree already contained the uncommitted two-tier application-card pass. It was preserved. This task did not change collectors, attribution, metric aggregation, package versions, GPU, history storage, actions, or unrelated pages.

### Reproduced causes

- The native Close, Minimize, Maximize, and Restore hit targets were present at 48 x 48 and their physical actions worked before the fix. The reproduced regression was that inactive caption glyphs were nearly invisible against the Light title-bar surface. `ExtendsContentIntoTitleBar` left caption drawing with `AppWindowTitleBar`, but the application never synchronized its foreground, inactive, hover, or pressed colors with the effective XAML theme.
- The existing one-minute CPU line rendered and changed in the initial live run, so a frozen renderer was not reproduced. Source review and deterministic tests reproduced four concrete correctness defects: timestamps were discarded, duplicate/out-of-order timestamps were not normalized, unavailable samples were removed so the line connected across missing intervals, and NaN/Infinity could create invalid geometry. The scaling floor `Math.Max(1, peak)` was also reused as the displayed peak, so a real all-zero history was described as a 1.0% peak.
- During implementation, creating `Windows.UI.ViewManagement.AccessibilitySettings` in this unpackaged Windows 10 process caused a startup fail-fast with `E_NOINTERFACE (0x80004002)` and `Microsoft.UI.Xaml.dll` exception `0xc000027b`. It was removed rather than caught. High Contrast is now read through `SPI_GETHIGHCONTRAST` in `MonitoringXS.Platform.Windows`, and High Contrast brushes use the user's dynamic `SystemColor*` values.

### Implementation and automated validation

- Caption-button colors now follow the effective Light or Dark theme. When High Contrast is active, all caption color overrides are cleared so Windows owns them.
- The chart now receives bounded timestamped samples. Projection sorts them, keeps the last duplicate timestamp, retains unavailable gaps, rejects negative and non-finite values, and keeps at most 60 samples. The renderer creates separate path figures for contiguous real intervals and reports the real peak independently from the minimum drawing scale.
- Appearance offers exactly System, Light, and Dark. The selection is stored atomically as `%LOCALAPPDATA%\MonitoringXS\appearance.txt`. System maps to `ElementTheme.Default`. No SQLite or Settings subsystem was added.

Commands executed:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

Observed command results:

- Sandboxed restore/build attempts encountered only NuGet TLS/credential `NU1301`. The same exact restore and build commands succeeded with normal network access. No package version changed and the global NuGet cache was not cleared.
- The full Release build succeeded with 0 warnings and 0 errors.
- All 114 tests passed with 0 failures and 0 skipped: Core 4, Application 5, Collectors 35, Storage 4, Integration 36, and App 30.
- New App tests cover timestamp ordering, last-duplicate selection, 60-sample bounding, unavailable gaps, invalid numbers, honest real-zero peak reporting, the three appearance values, invalid-preference fallback, and High Contrast resolving to System.

### Actual runtime observations

- System, Light, and Dark were selected through the accessible ComboBox without restarting. Light used cool neutral page, toolbar, primary metric, and card surfaces. Dark used distinct graphite page/navigation, blue-gray toolbar and card surfaces, cyan metric values, violet selection, and readable borders.
- `Dark` was written to the preference file, the app was closed and restarted, and UI Automation observed Dark selected after restart. The test then returned the preference to `System` through the UI.
- At 96 DPI, the native caption buttons remained separate from XAML content. Physical clicks verified Minimize, Maximize, Restore, and Close. Double-click maximized and restored the window. A drag moved it from `(122,32)` to `(182,62)`. The Alt+Space system menu was visibly observed with Restore, Move, Size, Minimize, Maximize, Close, and Alt+F4. Native hover and pressed states were captured, and the physical Close hit target exited `MonitoringXS.App` without a remaining process.
- The chart was observed continuously for 60.159 seconds with 55 UI Automation samples. CPU stayed real and non-zero, ranging from 2.7% to 19.2% in the recorded values. Seven distinct peak summaries were observed, and the process was responsive at every sample.
- Resizing to 900 x 700 left a visible 787 x 120 chart. Closing the Visual Studio Code tab and reopening it with keyboard Enter reconnected immediately to 60 real samples. The chart remained visible after scrolling and continued updating.
- Physical disk and Network continued to show `Unavailable` with `Access denied` in this unelevated run; neither was converted to zero or mixed with Process I/O.
- `SPI_GETHIGHCONTRAST` reported that High Contrast was not enabled in this session. High Contrast resource mapping compiled, but an actual High Contrast visual pass was not executed.

Screenshots created under ignored `.artifacts`:

- `theme-system.png`
- `theme-light.png`
- `theme-dark.png`
- `chart-repaired-dark.png`
- `caption-hover-dark.png`
- `caption-pressed-dark.png`
- `system-menu.png`

Actual 150% and 200% display scaling, a real High Contrast session, and Windows 11 Snap Layout were not available on this Windows 10/96-DPI machine. The branch therefore remains ready for focused visual review, not final Milestone 4 completion.

## 2026-07-23 sorting semantics and resolved-appearance validation

This pass clarified existing sorting and appearance behavior without changing collectors, attribution, metric models, the application-card layout, chart implementation, title-bar implementation, or package versions.

Commands executed:

```powershell
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

Build and test results:

- The first sandboxed build attempt failed during restore with NuGet TLS/credential `NU1301`. Repeating the same build command with normal network access succeeded in 00:01:30.92 with 0 warnings and 0 errors. The global NuGet cache was not cleared.
- All 150 tests passed with 0 failures and 0 skipped: Core 4, Application 5, Collectors 35, Integration 36, Storage 4, and App 66.
- New deterministic App tests cover all numeric fields in both directions, name and numeric smart defaults in the actual `MainWindowViewModel`, manual reversal, measured-before-unavailable ordering, deterministic name ordering when all values are unavailable, the comparable-data decision, direction accessibility text, and resolved appearance text.

Observed sorting behavior:

- Application name selected `A to Z` and reversed to `Z to A`.
- CPU, Memory, Process I/O, Physical Disk, Network, and process count each selected `Highest to lowest` as their new-field default and reversed to `Lowest to highest`.
- Every direction was stated in the visible button text and its accessible action name; the control was not arrow-only.
- Two installed cards remained between the Installed section header and the Portable section header. Three portable or unregistered cards remained after the Portable header.
- Memory and process-count ordering visibly reversed with the observed values. Some CPU and Process I/O orders matched name order when current values were equal; this was not treated as a sorting failure.
- Physical Disk and Network were `Access denied` for every visible application in this unelevated run. Both fields displayed `No comparable data`, kept deterministic A-to-Z ordering inside each section in both directions, and never converted unavailable values to zero. Selecting CPU, Memory, Process I/O, or process count removed the message.
- During a 12-second CPU-sort interval, Visual Studio Code remained selected in all 12 samples. While Monitoring XS remained the foreground window, the card also remained keyboard-focused in every sample. A previously opened Visual Studio Code tab remained present during the sort and refresh interval.
- Live CPU values and accessible card text changed every second, while card order changed only at the existing multi-second sort boundaries. The deterministic five-second policy remained covered by automated tests.

Observed appearance behavior:

- The Windows `AppsUseLightTheme` value was `1`. Selecting `System — follows Windows` resolved to `Currently Light` and persisted `System`.
- Forced Light resolved to `Currently Light` and persisted `Light`. Forced Dark resolved to `Currently Dark` and persisted `Dark`.
- The application was closed while Dark was selected and restarted. UI Automation observed `Application appearance. Dark. Currently Dark.` and the preference file still contained `Dark`. The preference was then returned through the UI to `System`, which resolved to `Currently Light`.
- A live Windows application-theme change was not performed, so System reacting to a Light-to-Dark OS change is not claimed. The implementation uses `ElementTheme.Default` and listens for `ActualThemeChanged`; validation here only confirms the current Light system state.

Regression observations:

- The Visual Studio Code tab closed through its native tab close button, keyboard Tab moved focus, and keyboard Enter reopened the application tab.
- The CPU chart was observed for 63.2 seconds with 30 UI Automation samples. CPU text produced 17 distinct observed values from 1.8% to 11.3%, chart peak text produced 6 distinct summaries from 11.3% to 40.1%, and the process was responsive in every sample.
- Native Maximize and Restore succeeded. Native Minimize entered the minimized state and Windows restored the window to normal.
- Double-click changed the window from normal to maximized and back. Dragging the title bar moved the normal bounds from `(52, 52, 1180, 760)` to `(102, 82, 1180, 760)`.
- Native Close ended `MonitoringXS.App` with no remaining application process. The first long-running `dotnet run` wrapper exceeded the validation tool's five-minute timeout, so its numeric exit code is not claimed. A second short persistence run completed in 00:00:50.3 with exit code 0.
- After both final exits, `logman` reported `Data Collector Set was not found` for `MonitoringXS.KernelMetrics.v1` and the retired `MonitoringXS.PhysicalDisk.v1` name.

No runtime product defect was reproduced during this pass. Actual System-on-Windows-Dark switching, a real High Contrast session, 150-200% display scaling, Windows 11 Snap Layout, and a broader screen-reader pass remain unexecuted. Milestone 4 remains in progress.

## 2026-07-24 latest baseline validation

This Phase 1 pass validated the committed baseline on
`feature/ui-polish-and-sorting`. It did not change source code, project files,
package versions, metric behavior, or milestone status.

Environment:

- Windows 10.0.19045, `win-x64`
- .NET SDK 10.0.302 and .NET runtime 10.0.10
- No installed .NET workloads
- No repository `NuGet.config` or package lock file was present; neither was
  created

Commands executed:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release
dotnet test MonitoringXS.sln -c Release
dotnet run --project .\src\MonitoringXS.App\MonitoringXS.App.csproj -c Debug
```

Build and test results:

- The sandboxed restore failed with NuGet repository-signature TLS/credential
  `NU1301` and vulnerability-feed `NU1900` warnings. Repeating the same restore
  with normal network access succeeded and reported all projects up to date.
  The global NuGet cache was not cleared.
- The Release build succeeded in 00:01:34.05 with 0 warnings and 0 errors.
- All 150 tests passed with 0 failures and 0 skipped: Core 4, Application 5,
  Collectors 35, Integration 36, Storage 4, and App 66.

Actual runtime observations:

- A visible and responsive `Monitoring XS` window opened at 1180 x 760.
- UI Automation exposed two installed applications and three portable or
  unregistered applications. Telegram Desktop and Visual Studio Code remained
  in the installed section. .NET, Google Chrome, and Monitoring XS remained in
  the portable or unregistered section.
- Multiprocess grouping was visible for Visual Studio Code (15 processes) and
  Google Chrome (13 processes). This confirms the grouped UI result observed in
  this run; it is not a synthetic PID-reuse validation.
- CPU, memory, and Process I/O were live. Across three samples five seconds
  apart, Visual Studio Code CPU changed from 12.2% to 0.2%, Chrome CPU changed
  from 11.8% to 9.0%, and the cards' accessible names changed with the sampled
  values.
- Process I/O remained a separately labelled secondary metric. Physical Disk
  and Network each displayed `Unavailable` with `Access denied`; neither was
  converted to zero or mixed with Process I/O.
- The Telegram Desktop application tab opened with keyboard Enter, closed
  through its tab close control, and reopened with Enter.
- Keyboard focus moved from the Telegram Desktop card to the Visual Studio Code
  card with the Down key.
- Advanced mode changed from Off to On and returned to Off through its
  Automation toggle. The accessible tree exposed the Advanced-mode notice while
  it was enabled.
- A 30-second smoke sample reported 0.385% average process CPU, 0.579% maximum
  process CPU, and 173.2 MB average working set. Working set ranged from
  172.9 MB to 173.3 MB, and the process responded in all 30 samples. This is a
  short runtime smoke measurement, not a formal performance benchmark.
- A standard `WM_CLOSE` request exercised the normal window-close path. Both
  `MonitoringXS.App` and its `dotnet run` parent exited, no application process
  remained, and neither `MonitoringXS.KernelMetrics.v1` nor
  `MonitoringXS.PhysicalDisk.v1` remained active.
- The detached launcher did not retain a numeric exit code, so exit code zero is
  not claimed. No Monitoring XS Application Error, .NET Runtime failure, or
  Windows Error Reporting entry was found for this run.

The actual baseline screenshot is
`.artifacts/Phase1Baseline/monitoringxs-baseline-2026-07-24.png`; it is ignored
by Git.

This run was unelevated, so elevated Physical Disk or Network availability was
not revalidated. Actual PID reuse, 150-200% display scaling, High Contrast,
Windows 11 Snap Layout, and a broad screen-reader pass were not executed.
Milestone 4 therefore remains in progress, and no change to
`docs/MILESTONES.md` was justified.
