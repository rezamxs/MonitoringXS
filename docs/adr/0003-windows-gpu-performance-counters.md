# ADR 0003: Use Windows GPU performance counters for application attribution

Date: 2026-07-26

Status: accepted provider; Milestone 3B implementation is under stabilization review

## Context

Monitoring XS needs real GPU engine utilization and GPU memory values for logical applications. The source must work with DirectX, OpenGL, Vulkan, video decode, compute, integrated GPUs, discrete GPUs, and systems with more than one adapter. It must not guess that a missing counter is zero.

The Windows options considered were:

- GPU performance counter sets consumed through PDH;
- DXGI adapter and video-memory APIs;
- graphics-kernel ETW;
- vendor-specific APIs.

`IDXGIAdapter3.QueryVideoMemoryInfo` reports budget and usage for the calling process. It cannot provide the same per-process view for every other application. DXGI remains useful for future adapter metadata, but it is not the per-application source in this milestone.

Graphics-kernel ETW can expose deeper scheduling activity, but it adds a second high-volume trace pipeline, more driver-version parsing, and a larger validation surface. Vendor APIs would produce different behavior on Intel, AMD, NVIDIA, virtual, and remote adapters.

Windows already exposes `GPU Engine` and `GPU Process Memory` performance counter sets from the WDDM graphics scheduler and memory manager. PDH can consume their English counter names without adding a runtime package.

## Decision

Milestone 3B uses one long-lived native PDH query with these wildcard counters:

- `\GPU Engine(*)\Utilization Percentage`
- `\GPU Process Memory(*)\Dedicated Usage`
- `\GPU Process Memory(*)\Shared Usage`

The query is created lazily, reused across one-second samples, and closed when the dependency-injection container is disposed. The first collection is `WarmingUp`. Missing counter sets, access denial, invalid data, and driver limitations remain explicit unavailable states.

Instance names are enumerated by Windows and parsed; paths are never guessed. An engine identity contains:

- process ID;
- adapter LUID;
- physical-adapter index;
- engine index;
- engine type.

Every attributed counter PID must match a current `ProcessInstanceId` containing the same PID and UTC process creation time. Process creation comes from absolute Windows `FILETIME`. No QPC-relative timestamp is compared with UTC. A matching PID with a different start time is rejected. A stale or inaccessible descendant may make a logical-application result partial, but its counters are never assigned by ancestry alone.

For a logical application, values from its processes are summed per identical adapter/physical-adapter/engine identity. The displayed GPU percentage is the busiest resulting engine across all adapters. Engine percentages are not summed across parallel engines or adapters because that can produce a misleading value above 100%.

Dedicated and shared memory are sums of the Windows per-process values. They are process-attribution values, not unique adapter totals. Cross-process shared allocations can appear in more than one process and can therefore be counted more than once. The UI and diagnostics state this limitation.

## Feasibility evidence

The development machine exposes two adapters:

- Intel HD Graphics 3000, driver `9.17.10.4459`;
- NVIDIA GeForce GT 525M, driver `21.21.13.6909`.

The counter provider exposed 86 GPU-engine instances and 70 GPU-process-memory counter paths across two adapter LUIDs. A live VLC workload on the NVIDIA LUID produced eight engine instances. Ten consecutive native samples observed a busiest-engine range of approximately 4.26% to 5.02%, 69,111,808 bytes of dedicated usage, and 913,408 bytes of shared usage. Idle Chrome processes without GPU instances produced measured zero only after the provider was healthy.

A PowerShell upper-bound probe sampled 156 paths in 20 one-second batches. It returned 3,120 valid values, used 1,156.25 ms of probe-process CPU time, and increased working set by 3,297,280 bytes. This includes PowerShell and repeated `Get-Counter` overhead and is not the product design.

The native persistent-query probe had a one-time initialization cost around 0.9-1.2 seconds on this old dual-GPU/driver combination. Later capture time was normally in the low-millisecond range, with occasional larger refresh samples. Product-level idle CPU and working set must still be measured during the final WinUI runtime validation.

An isolated Edge WebGL probe did not create a matching GPU counter instance on this machine. That result does not make the provider globally unsupported: VLC produced repeatable live values at the same time. It demonstrates that hardware acceleration, browser sandboxing, driver choice, and workload placement can determine whether a process has a GPU instance.

The focused stabilization pass on 2026-07-26 found and corrected a memory-attribution availability bug: dedicated and shared instance sets are now summed independently, so a valid value in one counter set cannot make the other set appear incomplete. Native PDH buffers are capped at 64 MiB and 65,536 items, duplicate and invalid values are rejected or marked partial, and concurrent capture/disposal is serialized and idempotent. The remaining checkpoint blockers are compatibility coverage and an actual operating-system PID-reuse observation; the provider does not expose a process-lifetime token inside a counter instance, so the first sample after process discovery cannot prove that an already-present same-PID instance belongs to the new lifetime.

## Hardware and driver limits

- Task Manager GPU data requires a WDDM 2.x-capable driver. Missing performance counter objects are `Unsupported`, not zero.
- Integrated GPUs can legitimately report zero dedicated memory and use shared system memory instead.
- Discrete GPUs can use both dedicated and shared memory.
- Multiple adapters remain separate through their LUID and physical-adapter index.
- Linked adapters can expose several physical indices under one scheduling link; the physical index is retained.
- Virtualized GPU activity may be owned by a host process such as `vmmem`, so guest attribution can be unavailable.
- Remote sessions, old drivers, software rendering, disabled acceleration, and protected or higher-integrity processes can limit per-process visibility.
- Microsoft documents a Windows 10 issue where per-process GPU memory counters can report a false increase on affected systems. Monitoring XS presents the Windows value and does not claim that it proves a leak or a unique allocation total.

## Consequences

The baseline implementation is vendor-neutral and needs no service, driver, permanent elevation, new package, or graphics ETW session. It can expose real utilization and memory on supported WDDM systems with bounded state.

The headline percentage deliberately matches the busiest-engine interpretation rather than a sum or average. Advanced diagnostics retain adapter/engine identity, source status, instance counts, PID-reuse rejection, inaccessible samples, invalid counters, and collection duration.

Future adapter names, capacity, budgets, and integrated/discrete labels may use DXGI metadata keyed by adapter LUID. That metadata is not required to make the current process metrics real and is not fabricated when unavailable.

## References

- [Microsoft DirectX team: GPUs in the Task Manager](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)
- [PDH counter and instance enumeration](https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhenumobjectitemsw)
- [Consuming counter data](https://learn.microsoft.com/windows/win32/perfctrs/consuming-counter-data)
- [GPU node and engine enumeration](https://learn.microsoft.com/windows-hardware/drivers/display/enumerating-gpu-nodes)
- [WDDM 2.0 and Windows 10](https://learn.microsoft.com/windows-hardware/drivers/display/wddm-2-0-and-windows-10)
- [IDXGIAdapter3::QueryVideoMemoryInfo](https://learn.microsoft.com/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo)
- [Known Windows GPU process-memory counter issue](https://learn.microsoft.com/troubleshoot/windows-client/performance/gpu-process-memory-counters-report-wrong-value)
