# Architecture

Monitoring XS uses a layered modular monolith. This keeps the version-one process model simple while isolating platform and privilege boundaries.

```text
MonitoringXS.App -> Application -> Core
                 -> DesignSystem
Application      -> Collectors -> Platform.Windows -> Core
                 -> Storage    -> Core
ElevatedHelper   -> Platform.Windows -> Core
```

## Runtime data flow

1. `IProcessDiscoveryService` uses one Toolhelp process snapshot, one top-level-window snapshot, and limited process handles to produce best-effort descriptors with a stable PID-plus-start-time key. Live PID/path details and visible executable metadata are bounded and revalidated without enumerating process modules every second.
2. `IApplicationAttributionService` combines Win32/MSIX catalogs, package identity/AppUserModelID, signatures, ancestry, known rules, and persistent user overrides into logical applications with evidence, confidence, and disposition.
3. Platform readers expose a single-handle CPU-time, working-set, and process-I/O snapshot with expected availability failures; collectors calculate deltas without depending on UI or calling Win32 directly.
4. `EtwPhysicalDiskEventSource` owns one real-time kernel session with only disk-I/O and thread keywords. It immediately converts ETW wall-clock timestamps to UTC, uses a bounded thread-to-PID map and bounded event queue, and never exposes raw QPC-relative time outside the Windows platform layer.
5. The physical-disk collector compares each event only with the UTC-normalized PID-plus-start-time identity, rejects pre-start events caused by PID reuse, and exposes loss/drop counters rather than silently treating incomplete data as complete.
6. `IMetricAggregationService` and `IPhysicalDiskAggregationService` aggregate only confidently attributed application processes. Process I/O and physical disk remain separate metric families.
7. The application coordinator samples only processes included in application totals, bounds live one-minute history to 512 application series, and publishes immutable snapshots. SQLite history remains deferred to Milestone 5.
8. ViewModels project snapshots into virtualized WinUI collections and bounded chart buffers.

## Failure model

Process exit, access denied, missing counters, ETW session conflicts/loss, and protected processes are expected states. Platform operations return structured availability/results. A collector failure degrades only its metric. Cancellation stops ETW processing and is propagated during shutdown and refresh replacement.

## Privilege boundary

The main app runs unelevated and never triggers UAC automatically. Kernel ETW may report `AccessDenied`; the UI preserves the remaining metrics and explains that approved elevation or Performance Log Users membership is required. A future helper accepts a versioned, allow-listed request over a local authenticated channel, performs one operation, reports a structured result, and exits. It will never expose general command execution.

## Version choices

- .NET 10 and C# latest stable language supported by that SDK.
- Windows App SDK 2.2.0 stable.
- Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5, centrally pinned and isolated to `MonitoringXS.Platform.Windows`.
- Compile against a current Windows SDK while declaring Windows 10 build 17763 as the minimum.
- Runtime guards are mandatory for APIs newer than the minimum OS.
- x64 is the primary target; identifiers and interop use pointer-size-safe types for future ARM64 support.
