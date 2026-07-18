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

No benchmark figures are claimed yet. Measurements must record hardware, OS build, build configuration, duration, sample interval, app count, CPU, working set, and database state.
