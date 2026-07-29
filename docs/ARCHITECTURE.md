# Architecture

Monitoring XS uses a layered modular monolith. This keeps the version-one process model simple while isolating platform and privilege boundaries.

```text
MonitoringXS.App -> Application -> Core
                 -> DesignSystem
Application      -> Collectors -> Platform.Windows -> Core
                 -> Storage    -> Core
ElevatedHelper   -> Platform.Windows -> Core
PrivilegedBroker -> Platform.Windows -> Core
```

## Runtime data flow

1. `IProcessDiscoveryService` uses one Toolhelp process snapshot, one top-level-window snapshot, and limited process handles to produce best-effort descriptors with a stable PID-plus-start-time key. Live PID/path details and visible executable metadata are bounded and revalidated without enumerating process modules every second.
2. `IApplicationAttributionService` combines Win32/MSIX catalogs, package identity/AppUserModelID, signatures, ancestry, known rules, and persistent user overrides into logical applications with evidence, confidence, and disposition.
3. Platform readers expose a single-handle CPU-time, working-set, and process-I/O snapshot with expected availability failures; collectors calculate deltas without depending on UI or calling Win32 directly.
4. `EtwPhysicalDiskEventSource` owns one shared real-time kernel session named `MonitoringXS.KernelMetrics.v1` with disk-I/O, thread, and network TCP/IP keywords. Physical-disk and network events use separate bounded queues. Network parser status is separate from physical-disk status, and recoverable network-event failures are counted without ending the shared session. ETW callbacks only normalize, count, and enqueue data; they never wait for a consumer. The bounded IRP map retains the original user initiator across multiple split completions and does not let a later PID 4 split-init overwrite that evidence.
5. The physical-disk collector compares each event only with the UTC-normalized PID-plus-start-time identity, rejects pre-start events caused by PID reuse, and calculates rates with an injected monotonic `TimeProvider`. Sub-10-ms captures retain bytes for the next valid interval instead of producing spikes. ETW loss and queue overflow keep session totals marked as lower bounds, and Advanced diagnostics expose source, queue, failure, latency, and last-event state.
6. The network collector uses the PID carried by typed kernel TCP/UDP send/receive events, normalizes event time to UTC before PID-reuse checks, and calculates rate windows with monotonic time. Sub-10-ms bytes are carried into the next valid interval. PID 0/4, malformed-PID, outside-application-set, and pre-start PID-reuse events remain unattributed. IP Helper owner-PID tables provide current TCP/UDP counts when both IPv4 and IPv6 snapshots succeed.
7. `WindowsGpuPerformanceCounterSource` owns one persistent native PDH query for wildcard GPU-engine and per-process dedicated/shared-memory counters. It enumerates adapter LUID, physical adapter, engine, and PID instance data, then validates every target PID against the current absolute UTC process creation `FILETIME`. Its query, five-second read-only ancestry cache, native buffer (64 MiB), item count (65,536), and lifetime tracker (32,768 process IDs) are bounded. A reused PID stays quarantined until a complete counter enumeration observes the old instance absent; an incomplete enumeration cannot clear that quarantine. The query is released idempotently with the application service provider. It starts no ETW session and requires no vendor package.
8. `IMetricAggregationService`, `IPhysicalDiskAggregationService`, `INetworkMetricAggregationService`, and `IGpuMetricAggregationService` aggregate only confidently attributed application processes. GPU values are summed per identical adapter/engine and the busiest engine becomes the headline percentage; parallel engines are not added together. Network aggregation retains bytes already observed for an exited helper while another process of the same logical application remains active. A process baseline is tracked independently of its current logical-application key, so reclassification moves only future deltas and never transfers historical bytes to the new application. Retained application state is capped at 512 active logical applications and is removed when the entire application leaves the snapshot. Totals beyond the cap are unavailable, not guessed; if capacity later becomes available, accumulation resumes from the bounded process baseline as a lower bound.
9. The application coordinator samples only processes included in application totals, bounds live one-minute history to 512 application series, publishes immutable snapshots, and best-effort enqueues each snapshot to `IMetricHistoryStore`. Storage errors and queue drops never interrupt live metrics.
10. `SqliteMetricHistoryStore` writes version-2 SQLite rows asynchronously in bounded batches. Logical application ID is paired with a SHA-256 process-lifetime key derived from PID plus UTC start time, so relaunches remain queryable without merging lifetimes. Raw rows are retained for one hour; older rows in the 24-hour retention window become five-minute buckets. Rate/gauge values are averaged, availability uses the worst state, and cumulative totals are never averaged or persisted as rates.
11. ViewModels project snapshots into virtualized WinUI collections and bounded chart buffers. History UI remains deferred.

## Failure model

Process exit, access denied, missing counters, ETW session conflicts/loss, queue overflow, and protected processes are expected states. Platform operations return structured availability/results. A collector failure degrades only its metric. Cancellation stops ETW processing and is propagated during shutdown and refresh replacement.

## Privilege boundary

The main app remains `asInvoker` and never triggers UAC during normal launch. `MonitoringXS.PrivilegedBroker` is an automatically started Windows Service running as `LocalSystem`; elevation is limited to installation/setup. LocalService reached `TraceEventSession.EnableKernelProvider` but failed with Win32 5, while the matching hardened LocalSystem service succeeded. The broker reuses the shared kernel ETW session and exposes only version-1 named-pipe hello, network-read, and physical-disk-read operations. The pipe name is a hash of user SID, optional logon SID, and session; its protected DACL is explicit and denies Network SID. Bounded frames/queues, one connection, request/idle timeouts, and reconnect-safe service-instance identifiers limit resource use. The broker authorizes the exact `MonitoringXS.App.exe` client, session, user SID, and every PID plus `StartTimeUtc` before and after each read. Responses are filtered to that process set, so cross-user and PID-reuse leakage is rejected. Broker failure maps to `Unavailable`/`Partial`; CPU, memory, process I/O, and GPU remain independent.

## Version choices

- .NET 10 and C# latest stable language supported by that SDK.
- Windows App SDK 2.2.0 stable.
- Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5, centrally pinned and isolated to `MonitoringXS.Platform.Windows`.
- Compile against a current Windows SDK while declaring Windows 10 build 17763 as the minimum.
- Runtime guards are mandatory for APIs newer than the minimum OS.
- x64 is the primary target; identifiers and interop use pointer-size-safe types for future ARM64 support.
