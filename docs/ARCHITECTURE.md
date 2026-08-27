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
9. The application coordinator samples only processes included in application totals, bounds live one-minute history to 512 application series, publishes immutable snapshots, and best-effort enqueues each accepted snapshot plus its successful base discovery result to `IMetricHistoryStore`. Storage errors and queue drops never interrupt live metrics.
10. `SqliteMetricHistoryStore` writes version-3 SQLite rows asynchronously through a bounded queue. Its focused session reconciler owns logical-application sessions and PID-plus-UTC-start-time process sessions. Base-enumerated PIDs keep sessions alive when descriptor/path/metadata reads are partial; fatal discovery produces no accepted snapshot and therefore no false closure. Restart continues an application session only when a prior process identity is still observed. Session heartbeats persist at most every 30 seconds while starts, ends, PID reuse, and samples share the capture transaction. Migrated v2 samples keep their old hash only as nullable legacy continuity metadata and receive no fabricated session identity. Raw rows are retained for one hour; older rows in the 24-hour retention window become five-minute buckets grouped by application-session boundary.
11. ViewModels project snapshots into virtualized WinUI collections and bounded chart buffers. `HistoryPageViewModel` lists persisted logical applications, issues cancellable 5-minute/15-minute/1-hour/3-hour/6-hour/12-hour/24-hour queries off the UI thread, rejects stale results after rapid selection changes, and decimates each displayed series to at most 360 points while retaining endpoints, gaps, and extrema. The History page normalizes stable ascending UTC timestamps, keeps the last duplicate, and uses the selected range for X coordinates. Hover mapping projects pointer X onto that same time domain and selects the nearest displayed sample by timestamp; it does not query storage. Unavailable/non-numeric partial samples, application-session changes, legacy continuity changes, and large sampling gaps split paths; numeric partial samples remain plotted and labelled partial. All-unavailable query windows present Empty/Unavailable rather than a successful zero series.
12. `JsonApplicationSettingsStore` owns the single version-1 per-user settings document under `%LOCALAPPDATA%\MonitoringXS\settings.json`. It validates only predefined sampling, retention, and theme values, accepts unknown fields for the current version, rejects newer versions, quarantines corrupt/invalid files, and writes through one serialized temporary-file replacement. `LiveRefreshCadence` wakes the existing single-execution loop when cadence changes. The SQLite store receives retention policy changes for later bounded maintenance; no schema migration or synchronous deletion is involved. See [ADR 0006](adr/0006-versioned-per-user-settings.md).
13. `IProcessActionService` is the authoritative typed boundary for selected-process inspection, End Task, bounded End Process Tree, and Open File Location. `WindowsProcessActionService` revalidates PID, UTC start time, process name, and executable path where available immediately before action. Destructive work uses limited query/terminate/synchronize rights, checks Windows critical/protection state, refuses Monitoring XS, its Broker, PID 0/4, and unverifiable targets, and confirms exit before success. Tree snapshots are bounded to three passes and terminate verified leaves before the root. Clipboard formatting stays in the app layer and copies only allowlisted identity and aggregate metric fields.
14. Running Apps search filters the existing logical-application card instances by already-collected display, executable, publisher, and packaged identity metadata. Sorting remains section-local, rate-limited during live refresh, availability-aware, and persisted through the single atomic settings document.
15. `MetricExplanationService` is the app-layer authority for localized beginner descriptions, advanced source/cadence/completeness text, and safe reason mapping from existing typed collector state. `DiagnosticsPageViewModel` projects the latest dashboard snapshot, collector diagnostics, Broker probe, and SQLite diagnostics; it never starts a second capture loop.

## Deployment

The release deployment is one x64, per-machine WiX 7 MSI containing
self-contained application and Broker payloads. Native MSI tables own files,
shortcuts, upgrades, repair, rollback, uninstall, and Broker service lifecycle.
One fixed-purpose custom-action assembly captures the interactive SID/session
and applies the service SID type that MSI cannot express reliably; it has no
command runner or extensibility surface. There is no bootstrapper, updater, or
second installer system. See [Windows installer](INSTALLER.md).

## Failure model

Process exit, access denied, missing counters, ETW session conflicts/loss, queue overflow, and protected processes are expected states. Platform operations return structured availability/results. A collector failure degrades only its metric. Cancellation stops ETW processing and is propagated during shutdown and refresh replacement.

## Privilege boundary

The main app remains `asInvoker` and never triggers UAC during normal launch. `MonitoringXS.PrivilegedBroker` is an automatically started Windows Service running as `LocalSystem`; elevation is limited to installation/setup. LocalService reached `TraceEventSession.EnableKernelProvider` but failed with Win32 5, while the matching hardened LocalSystem service succeeded. The broker reuses the shared kernel ETW session and exposes only version-1 named-pipe hello, network-read, and physical-disk-read operations. The pipe name is a hash of user SID, optional logon SID, and session; its protected DACL is explicit and denies Network SID. Bounded frames/queues, one connection, request/idle timeouts, and reconnect-safe service-instance identifiers limit resource use. The broker authorizes the exact `MonitoringXS.App.exe` client, session, user SID, and every PID plus `StartTimeUtc` before and after each read. Responses are filtered to that process set, so cross-user and PID-reuse leakage is rejected. Broker failure maps to `Unavailable`/`Partial`; CPU, memory, process I/O, and GPU remain independent.

Before attempting the pipe handshake, the client reads only the broker service
status through SCM. Normal UI maps the result to a small safe vocabulary:
service not installed, service stopped, connection failed, protocol mismatch,
ETW unavailable, or no attributed activity yet. Detailed paths, SIDs, and native
exceptions remain restricted to validation diagnostics.

Process actions never cross the Broker boundary. The main app performs them as
`asInvoker`; Access Denied is a normal typed result and never triggers
elevation. The Broker protocol, service ACL, and service lifecycle contain no
process-action operation.

Persistent development/operator setup uses the tracked
`scripts/privileged-broker/Manage-PrivilegedBroker.ps1` entry point. It publishes
the current Release broker, creates only the fixed automatic LocalSystem service
with a dedicated service SID, verifies its binary/configuration, and confines
removal to its fixed ProgramData directory. It never launches or elevates the
main application.

## Version choices

- .NET 10 and C# latest stable language supported by that SDK.
- Windows App SDK 2.2.0 stable.
- Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5, centrally pinned and isolated to `MonitoringXS.Platform.Windows`.
- Compile against a current Windows SDK while declaring Windows 10 build 17763 as the minimum.
- Runtime guards are mandatory for APIs newer than the minimum OS.
- x64 is the primary target; identifiers and interop use pointer-size-safe types for future ARM64 support.
