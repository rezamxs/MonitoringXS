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

1. `IProcessDiscoveryService` produces best-effort process descriptors with a stable process-instance key composed of PID and start time.
2. `IApplicationAttributionService` classifies descriptors into logical applications with evidence, confidence, and disposition.
3. Platform readers expose Windows counter snapshots and expected availability failures; collectors calculate CPU and process-I/O deltas without depending on UI types.
4. `IMetricAggregationService` aggregates only confidently attributed application processes.
5. The application coordinator publishes immutable snapshots and writes batched history.
6. ViewModels project snapshots into virtualized WinUI collections and bounded chart buffers.

## Failure model

Process exit, access denied, missing counters, and protected processes are expected states. Platform operations return structured availability/results. A collector failure degrades only its metric. Cancellation is propagated during shutdown and refresh replacement.

## Privilege boundary

The main app runs unelevated. A future helper accepts a versioned, allow-listed request over a local authenticated channel, performs one operation, reports a structured result, and exits. It will never expose general command execution.

## Version choices

- .NET 10 and C# latest stable language supported by that SDK.
- Windows App SDK 2.2.0 stable.
- Compile against a current Windows SDK while declaring Windows 10 build 17763 as the minimum.
- Runtime guards are mandatory for APIs newer than the minimum OS.
- x64 is the primary target; identifiers and interop use pointer-size-safe types for future ARM64 support.
