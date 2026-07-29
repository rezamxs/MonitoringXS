# Performance targets and measurement

Release targets are idle CPU below 1% on a typical modern system, normal working set below about 200 MB, fast startup, and responsive UI during process churn.

Implementation rules:

- one-second sampling with cancellation and drift-aware timing;
- metadata/signature/icon/package caches with bounded eviction;
- no per-refresh signature verification or full metadata reload;
- bounded in-memory history and bounded storage queues;
- virtualized application and process lists;
- batched SQLite writes with WAL and prepared statements;
- no permanent elevation or decorative GPU animation;
- collector timing, dropped samples, queue depth, DB latency, and cache hit rate exposed in internal diagnostics.
- SQLite history uses a 256-batch bounded queue, 32-snapshot transactions, WAL when supported, one-hour raw retention, five-minute downsampling, and a 64 MiB logical size policy. Queue drops and write failures are diagnostic counters; live collection does not wait on SQLite.
- History range queries run off the UI thread, rapid selection changes cancel and supersede older requests, and each of the 11 chart series is decimated to at most 360 displayed points while retaining endpoints, global extrema, PID/time gaps, and availability gaps where capacity permits. Projection also collapses effectively identical X coordinates before WinUI geometry creation.
- physical-disk ETW callbacks use a non-blocking bounded queue of 16,384 events; overflow is counted and reported as partial data;
- thread-to-PID state is capped at 32,768 entries and removed on thread end or cleared after ETW loss;
- init-to-completion IRP correlation is capped at 32,768 entries and cleared after ETW loss;
- one fixed ETW session is started lazily, retried no more than once per minute after failure, and stopped on application shutdown.
- network ETW callbacks use a separate non-blocking bounded queue of 16,384 events; overflow is counted and reported with lower-bound semantics;
- TCP/UDP owner-PID table reads reject buffers larger than 16 MiB and run outside ETW callbacks.
- GPU sampling reuses one native PDH wildcard query; it does not spawn `Get-Counter`, WMI, a vendor tool, or a graphics ETW session each second.
- the read-only process-parent snapshot used only to detect unassignable GPU descendants is cached for five seconds; it never authorizes attribution without PID/start-time validation.

Measurements must record hardware, OS build, build configuration, duration, sample interval, app count, CPU, working set, and database state.

## 2026-07-29 History chart and broker runtime pass

The fresh x64 Release app used an 1180 x 760 window. The validation monitor
reported 96 DPI (100%); this machine did not expose a safe per-application way
to emulate 150% display scaling, so 150% remains a follow-up validation limit.
History range query/display timings were `84/4 ms` (15 minutes), `80/2 ms`
(1 hour), `128/4 ms` (6 hours), and `87/8 ms` (24 hours), with 0, 0, 352, and
352 displayed chart points. The run closed normally with zero remaining app
processes. Four ignored PNG screenshots and `result.json` are under
`.artifacts/validation/history-chart-fix`.

## 2026-07-19 focused runtime smoke measurement

This is a smoke measurement, not a benchmark claim:

- Windows 10 Pro 22H2 build 19045.6466, Intel Core i7-2630QM, 8 logical processors.
- x64 Release, one-second sampling, no metric-history database, four visible logical applications in the UI and approximately 30 attributed processes during the measurement.
- Working set after warm-up: 154.9 MB.
- First 30-second post-start phase while tiered JIT/caches were still settling: 1.335% of total CPU capacity.
- Following 30-second steady phase with one live application tab open: 0.573% of total CPU capacity.

The same desktop-session probe reduced warm capture time from 104-294 ms to 20-85 ms after replacing repeated module/window enumeration and per-process double-handle metric reads. Longer profiling across additional hardware remains release work.

## 2026-07-21 elevated physical-disk smoke measurement

This was an active disk-workload smoke on Windows 10 Pro build 19045.6466 with 8 logical processors, x64 Release, one-second application sampling, and no history database; it is not an idle-performance claim.

- Low-intrusion lifecycle pass: 2.252% of total machine CPU capacity during the controlled workload, maximum working set 163,106,816 bytes, maximum handle count 911, and responsive at every sampled point.
- Instrumented UI pass: maximum observed ETW rate 198 events/s, maximum queue depth 895 of 16,384, 0 queue drops, 0 ETW-lost events, configured session buffer budget 32 MB, and `logman` values of 0 buffers lost and 7 buffers written.
- The workload exited normally, the window accepted a normal close, the app and `dotnet run` exited with code 0, and the ETW session was absent afterward in the low-intrusion pass.

The heavier UI Automation pass measured 3.498% total CPU capacity, 180,232,192-byte maximum working set, and 953 handles, but ended in a WinUI/XAML fail-fast while repeatedly scrolling and querying the virtualized list. These figures are retained as diagnostic evidence, not used as the clean lifecycle or responsiveness result.

## 2026-07-21 post-layout-cycle idle measurement

After the sparkline resize redraw was moved out of the active XAML layout pass, the x64 Release application was measured without UI Automation or a workload. The environment remained Windows 10 Pro build 19045.6466 with 8 logical processors and one-second monitoring updates.

- Warm-up: 30 seconds.
- Steady interval: 60.739 seconds with 61 working-set samples.
- Process CPU time: 3.09375 seconds, equal to 0.637% of total machine CPU capacity.
- Working set: 149,823,488 bytes minimum, 152,630,524 bytes average, 158,068,736 bytes maximum, and 153,739,264 bytes final.
- The UI was responsive at every sample, normal close succeeded, `dotnet run` returned 0, and Event Viewer recorded no new crash.

The result meets the current below-1%-idle-CPU and approximately-200-MB working-set targets on this validation machine. Broader hardware profiling remains release work.

## 2026-07-21 Milestone 3A unelevated idle measurement

The x64 Release application was measured after adding the network collector. Kernel ETW returned `AccessDenied`, so this is an idle UI/process measurement and not a network-workload performance claim.

- Warm-up: 30.517 seconds.
- Steady interval: 60.675 seconds with 60 working-set samples.
- CPU: 0.444% of total eight-logical-processor capacity.
- Working set: 151,945,216 bytes minimum, 154,113,229 bytes average, 155,783,168 bytes maximum, and 151,961,600 bytes final.
- The process was responsive for every sample.

The automation service could not complete the requested close interaction after the measurement, so clean shutdown is not claimed for this run. `logman` confirmed that no `MonitoringXS.PhysicalDisk.v1` ETW session was active.

## 2026-07-24 physical-disk stabilization smoke

This was an unelevated 30-second steady sample after the Debug application had completed startup and warm-up. Windows 10 build 19045, 8 logical processors, four visible logical applications, and no history database were observed.

- CPU averaged 0.574% of total machine capacity and peaked at 1.827%.
- Working set averaged 182,593,399 bytes (174.1 MiB) and peaked at 184,037,376 bytes (175.5 MiB).
- The process responded in all 30 samples.
- Kernel ETW returned `AccessDenied`, so event rate, queue depth, queue drops, and ETW loss were all observed as zero for this run; this is not an elevated disk-workload measurement.
- Normal close removed the application and `dotnet run` processes, and no Monitoring XS kernel session remained.

Compared with the latest 0.385% CPU / 173.2 MiB baseline, CPU was 0.189 percentage points higher and average working set was 0.9 MiB higher. The CPU result still meets the below-1% idle target; active Chrome and Visual Studio Code workloads differed between the two short samples, so this single smoke does not establish a regression.

## 2026-07-24 elevated split-IRP attribution validation

This Debug validation ran from a manually approved Administrator PowerShell on Windows 10 build 19045. A controlled 20-second workload wrote and read 159,383,552 process-I/O bytes in each direction. Its read path was mostly cached; Physical Disk therefore did not mirror the Process I/O read counter.

- The workload card reached 5.7 MB/s Physical Disk write while Process I/O remained separately displayed at approximately 8.0 MB/s read and write.
- The attributed workload session reached 37.1 MB write and 4.0 KB read. The kernel source observed 156.2 MB write and 475.1 MB read across the whole machine; events owned by PID 4 or processes outside the current application set were not guessed into the workload.
- Maximum ETW event rate was 1,488 events/s. Maximum queue depth was 1,492 of 16,384, with 0 local drops and 0 ETW-lost events.
- Maximum collector processing latency observed in Advanced diagnostics was 1.066 ms.
- After workload completion and cooldown, the 30-second steady sample averaged 0.508% CPU and peaked at 2.325%.
- Working set averaged 188,336,674 bytes (179.6 MiB) and peaked at 193,048,576 bytes (184.1 MiB).
- The app responded in all 48 workload and steady-state checks. Normal close returned app and `dotnet run` exit code 0, and no Monitoring XS process or ETW session remained.

The average remains below the 1% CPU target and working set remains below the approximate 200 MB target. Peak CPU is an instantaneous one-second sample, not the idle average.

## 2026-07-24 network diagnostics and browser workload pass

This Debug pass used Windows 10 build 19045 with 8 logical processors, one-second application sampling, no history database, and a controlled Chrome page that requested 10,000,000 receive bytes and sent 2,097,152 bytes. UI Automation queried all application cards every second, so its CPU figures include instrumentation pressure and are not an idle benchmark.

- Unelevated, the shared kernel session returned `AccessDenied`. Across the measured idle/workload/cooldown interval, average working set was 176,455,407 bytes (168.28 MiB), peak working set was 180,609,024 bytes (172.24 MiB), and the app responded in every sample.
- Elevated before the retained-total fix, instrumented CPU averaged 1.811% during the 30-second idle phase, 2.204% during the 30-second workload phase, and 3.480% during the 15-second cooldown. Overall post-warm-up working set averaged 179,842,307 bytes (171.51 MiB) and peaked at 185,708,544 bytes (177.11 MiB).
- Chrome reached 2.8 MB/s receive and 2.0 MB/s send. The final Advanced diagnostic snapshot observed 35,132 network events, a current rate of 136 events/s, queue depth 138 with a session maximum of 1,183 out of 16,384, 0 queue drops, 0 ETW-lost events, 0 processing failures, and 0 unsupported versions.
- Collector processing latency was 1.330 ms average and 18.202 ms maximum in that snapshot. The shared source observed 45.1 MB send and 89.2 MB receive across the machine; 3,684 events were attributed and 31,448 remained unattributed, including 12 system events and 31,436 events outside the active application set.
- Normal close returned exit code 0 for both the application and `dotnet run`; no Monitoring XS process or ETW session remained.

The instrumented average is above the below-1% idle target and cannot replace the earlier low-intrusion 0.508%/179.6 MiB Phase 2 measurement. The new aggregation state is bounded to 512 active logical applications and removes per-process entries when helpers exit. The accepted post-fix elevated measurement below records the required 30/60/60/30 lifecycle pass; it is still an instrumented acceptance run rather than a standalone idle benchmark.

## 2026-07-25 final elevated Network acceptance measurement

This was the accepted post-fix Phase 3A run on Windows 10 build 19045. The runner sampled `Process.TotalProcessorTime` and `WorkingSet64` once per second and used UI Automation only at lifecycle checkpoints. It included 30 seconds of warm-up, 60 seconds idle, 60 seconds of controlled Edge and disk workload, and 30 seconds cooldown.

- Warm-up: 2.425% average CPU, 12.183% peak CPU, 179,316,190-byte average working set, 184,696,832-byte peak working set.
- Idle: 0.600% average CPU, 2.503% peak CPU, 177,796,642-byte average working set, 182,439,936-byte peak working set.
- Workload: 0.749% average CPU, 4.190% peak CPU, 193,305,122-byte average working set, 200,978,432-byte peak working set.
- Cooldown: 0.398% average CPU, 1.977% peak CPU, 203,075,174-byte average working set, 208,470,016-byte peak working set.
- Overall post-warm-up average working set was 189,055,741 bytes and the maximum was 208,470,016 bytes.
- The UI remained responsive at every sample, the controlled workload exited, and the application shut down with exit code 0.

These are instrumented acceptance measurements rather than a low-intrusion idle benchmark. The earlier low-intrusion measurements remain the appropriate comparison for the idle target.

## 2026-07-26 GPU feasibility and WinUI runtime measurement

This Debug validation ran on Windows 10 build 19045 with 8 logical processors, an Intel HD Graphics 3000 adapter, an NVIDIA GeForce GT 525M adapter, one-second application sampling, and no history database. The UI Automation harness queried cards at five-second checkpoints. VLC was already producing a real hardware GPU workload; the validation did not alter or close that user process.

- The persistent native query exposed 95 engine instances, 2 matching process-memory instances, and 2 adapter LUIDs in the final Advanced snapshot.
- VLC used the NVIDIA adapter's 3D engine. UI samples observed 4.2-5.0% busiest-engine utilization, 65.9 MB dedicated memory, and 892.0 KB shared memory.
- Final diagnostics reported 34 target and 34 sampled processes, 0 PID-reuse rejections, 0 inaccessible process samples, 0 unassigned descendant counters, 0 invalid counter values, and 5.113 ms collection time.
- Warm-up (30 samples): 0.770% average CPU, 2.011% peak CPU, 179,612,331-byte average working set, and 182,599,680-byte peak working set.
- Steady state (60 samples): 0.760% average CPU, 1.997% peak CPU, 181,532,535-byte average working set, and 182,751,232-byte peak working set.
- The process responded in all 90 samples. The application and `dotnet run` parent both exited after a normal close, and neither Monitoring XS ETW session remained.

This is one old integrated/discrete driver combination, not a cross-vendor release benchmark. AMD, newer Intel/NVIDIA drivers, Windows 11, remote sessions, virtual GPUs, and an unsupported WDDM path still need separate runtime coverage.

### 2026-07-26 GPU stabilization rerun

The focused stabilization rerun used the same Windows 10 build 19045 machine after the independent dedicated/shared-memory fix. The Release build had 0 warnings and 0 errors, and the full Release suite passed 253 tests. The WinUI Debug harness ran a 30-second warm-up followed by two 30-second steady intervals.

- Warm-up: average CPU `0.8005%`, peak `2.7648%`, average working set `186,729,540` bytes, peak `187,961,344` bytes.
- Steady interval 1: average CPU `0.6748%`, peak `1.8521%`, average working set `184,987,921` bytes, peak `187,224,064` bytes.
- Steady interval 2: average CPU `0.6444%`, peak `1.8299%`, average working set `184,683,315` bytes, peak `187,478,016` bytes.
- UI Automation reported the app responsive in all 90 samples.
- VLC remained a real 3D workload: utilization ranged from `3.9%` to `5.0%`, dedicated memory was `45.1 MB`, and shared memory was `892 KB`.
- The final diagnostics snapshot reported 95 engine instances, 2 process-memory instances, 2 adapters, 0 PID-reuse rejections, 0 inaccessible samples, 0 unassigned descendants, 0 malformed/duplicate/invalid instances, and `2.606 ms` collection time.

A controlled Edge WebGL profile was also run while the app was open. Edge was attributed as its own logical application and showed real CPU/memory activity, but its GPU card remained `0.0%, 0 B dedicated · 0 B shared`; the provider probe collected 0 engine and 0 process-memory rows for that controlled Edge tree. Video-decode and OS PID-reuse scenarios were not observed on this machine. These are workload/driver and coverage limitations, not synthetic GPU values.
