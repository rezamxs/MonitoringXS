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
- physical-disk ETW callbacks use a non-blocking bounded queue of 16,384 events; overflow is counted and reported as partial data;
- thread-to-PID state is capped at 32,768 entries and removed on thread end or cleared after ETW loss;
- init-to-completion IRP correlation is capped at 32,768 entries and cleared after ETW loss;
- one fixed ETW session is started lazily, retried no more than once per minute after failure, and stopped on application shutdown.
- network ETW callbacks use a separate non-blocking bounded queue of 16,384 events; overflow is counted and reported with lower-bound semantics;
- TCP/UDP owner-PID table reads reject buffers larger than 16 MiB and run outside ETW callbacks.

Measurements must record hardware, OS build, build configuration, duration, sample interval, app count, CPU, working set, and database state.

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
