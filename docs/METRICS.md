# Metric semantics

## Sampling

Live sampling defaults to one second. Each sample carries a UTC wall-clock timestamp. PID plus process start time identifies a process instance and prevents PID-reuse contamination.

## CPU

Per-process CPU is computed from the delta in total process CPU time divided by elapsed wall time and logical processor count. The Beginner value therefore represents percentage of total machine CPU capacity. The first sample is unavailable because no valid delta exists.

## Memory

Beginner Mode displays working set bytes. Advanced fields may add private working set, private bytes, commit, and peak working set when available. Aggregation sums current included process values.

## Process I/O

The current vertical slice reads process CPU time, working set, and `GetProcessIoCounters` through one limited Windows process handle behind a platform abstraction. It calculates read/write bytes per second from cumulative counter deltas and retains cumulative read/write bytes and operation counts. These counters cover all I/O operations performed by a process, so the product labels them **Process I/O**, not disk activity. A first sample is `WarmingUp`; access-denied and failed reads remain unavailable.

If one process in a logical application cannot be sampled, an aggregate based on the remaining processes is marked `Partial` and displayed as a lower bound. It is never presented as a complete application total.

## Physical disk (ETW)

Physical-disk read/write bytes and operation counts come from kernel ETW disk-init and disk-completion events. The Windows layer correlates each bounded IRP identity from init to completion, maps the issuing thread to a PID when needed, normalizes the ETW `TimeStamp` to UTC, and emits no QPC-relative timestamp. One initiating IRP can produce multiple split completion events, so the bounded map retains the original user initiator until a later user init replaces it or normal capacity eviction removes it. A later split-init from PID 4 does not overwrite an already known user initiator. If a thread was already alive when the session began, a limited thread handle is used only long enough to query its owning PID and is closed immediately. The collector accepts an event only when its UTC timestamp is at or after the matching `ProcessInstanceId.StartTimeUtc`; raw QPC and UTC values are never compared.

Read/write rates use attributed bytes observed during the actual monotonic capture interval. UTC remains the sample/event domain for display and PID-reuse checks, but wall-clock changes do not affect elapsed-rate calculations. The collector requires at least 10 ms of monotonic elapsed time; a shorter interval remains `WarmingUp`, retains its observed bytes, and includes them in the next valid rate instead of producing a spike. Session totals begin when this Monitoring XS ETW source starts; they are not lifetime process counters. A healthy interval with no event is a real zero after the first `WarmingUp` capture. These metrics are labelled **Physical disk (ETW)** and remain distinct from **Process I/O**.

ETW access denied, session conflict, cancellation, loss, queue overflow, and unattributed events are explicit states. ETW loss or local queue overflow makes retained session totals persistently `Partial` lower bounds until the collector session resets. A later complete interval may again expose an `Available` rate, but its already-incomplete session total is never promoted back to complete. Global events assigned to PID 4 or another process outside the current application process set remain visible as unattributed diagnostics and are not reassigned to a user application. They do not incorrectly downgrade an otherwise complete mapped application; `complete` means no known ETW/queue loss in the retained attributed stream, not that every system disk completion had a user-process owner. A correctly rejected pre-start event belongs to the old PID instance and likewise does not downgrade the current instance. When ETW reports lost events, the current event batch plus thread and IRP maps are discarded before further attribution to avoid stale-thread/PID contamination.

Advanced diagnostics expose aggregate read/write event and byte counts, ETW loss, local queue depth/drop counts, unattributed events, PID-reuse rejections, metadata lookup failures, session-start/access-denied failures, collector processing latency, last retained event time, collector status, and whether retained totals are lower bounds. These are session diagnostics, not additional user activity data.

Primary references: [Microsoft DiskIo events](https://learn.microsoft.com/windows/win32/etw/diskio), [DiskIo read/write payload](https://learn.microsoft.com/windows/win32/etw/diskio-typegroup1), [DiskIo init IRP and issuing thread](https://learn.microsoft.com/windows/win32/etw/diskio-typegroup2), [ETW timestamp clocks](https://learn.microsoft.com/windows/win32/etw/wnode-header), and [ETW session properties/loss counters](https://learn.microsoft.com/windows/win32/api/evntrace/ns-evntrace-event_trace_properties).

A future `Current I/O Share` is the application's share of attributed application disk I/O, not drive active time.

## Network (ETW)

Download/Receive and Upload/Send bytes come from typed kernel TCP/UDP send and receive events for IPv4 and IPv6. Microsoft defines the event `size` field as packet size, so these values are bytes observed by the kernel Network ETW mechanism. They are not guaranteed to be useful application payload, physical-adapter wire bytes, router counters, or ISP-billed traffic. Retransmissions, loopback emission, protocol overhead, batching, hardware offload, and the comparison tool's counting point can produce different totals. Monitoring XS reads the payload PID and byte count but does not retain packet payloads, URLs, hostnames, ports, or local/remote addresses.

The Windows layer converts the ETW timestamp to UTC before the collector compares it with `ProcessInstanceId.StartTimeUtc`; raw QPC values are never compared with UTC. Rate denominators use monotonic elapsed time. The first healthy capture is `WarmingUp`, intervals below 10 ms retain bytes for the next valid interval, and a later healthy interval with no traffic is a real zero. Application totals begin with the current Monitoring XS collector session and are not lifetime process counters. Bytes from an exited helper remain in the total while another process of that logical application stays active. A process reclassified into another logical application contributes only later deltas to the new application; its historical total is not moved. When the entire logical application exits, its retained state is removed and a later restart begins a new total.

The network queue is bounded at 16,384 records. ETW event loss, local queue overflow, event-processing failure, unsupported event version, collector restart, or cancellation after a batch has been drained makes the affected interval `Partial`; retained totals remain lower bounds. Access denied, unsupported platforms, same-name session conflicts, resource exhaustion, and collector errors remain explicit and never become zero. PID 0/4, unknown PID payloads, events outside the active application set, and pre-start PID-reuse events are counted but never guessed into a user application. Excluding unrelated system/out-of-set traffic does not by itself make a correctly attributed application rate partial.

Logical-application retained state is capped at 512 simultaneously active applications. If that capacity is exhausted, the affected session totals are `Unavailable` instead of exposing raw process counters as a misleading application total. The current process baselines remain bounded by the active attributed process snapshot. If a slot later opens, accumulation resumes from that baseline and is marked `Partial` because bytes observed while the application state was not retained cannot be reconstructed.

Advanced diagnostics report total/send/receive events, TCP and UDP send/receive events, IPv4/IPv6 events, source send/receive bytes, attributed and unattributed categories, PID-reuse rejection, session-start/access-denied/processing failures, unsupported versions, event rate, queue depth/capacity/drop count, ETW loss, average/maximum collector latency, last successful event time, status/reason, and retained-total completeness. The typed parser subscribes to TCP/UDP send and receive for both address families. Future event versions that do not reach those typed callbacks cannot be positively identified; `UnsupportedEventVersions = 0` means none were detected, not proof that the operating system emitted no unknown version.

Current TCP connection and UDP endpoint counts use bounded owner-PID IP Helper table snapshots. A count is shown only when both IPv4 and IPv6 tables for that protocol were read successfully; otherwise that count is unavailable.

Primary references: [Microsoft TCP/IP ETW events](https://learn.microsoft.com/windows/win32/etw/tcpip), [TCP IPv4 send payload](https://learn.microsoft.com/windows/win32/etw/tcpip-sendipv4), [TCP IPv6 send payload](https://learn.microsoft.com/windows/win32/etw/tcpip-sendipv6), [Microsoft UDP/IP ETW events](https://learn.microsoft.com/windows/win32/etw/udpip), and [ETW session loss counters](https://learn.microsoft.com/windows/win32/api/evntrace/ns-evntrace-event_trace_properties).

## Services

Services and related background components are excluded by default. Advanced opt-in totals must be visibly labelled and must never silently replace the default calculation model.

## Retention target

- 0-10 minutes: about 1 second.
- 10-60 minutes: about 5 seconds.
- 1-6 hours: about 30 seconds.
- 6-24 hours: about 1 minute.

Downsampling preserves averages and peaks. Disk writes are batched in transactions; expired data is removed off the UI thread.
