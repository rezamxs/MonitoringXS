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
