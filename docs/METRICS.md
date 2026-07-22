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

Physical-disk read/write bytes and operation counts come from kernel ETW disk-init and disk-completion events. The Windows layer correlates each bounded IRP identity from init to completion, maps the issuing thread to a PID when needed, normalizes the ETW `TimeStamp` to UTC, and emits no QPC-relative timestamp. If a thread was already alive when the session began, a limited thread handle is used only long enough to query its owning PID and is closed immediately. The collector accepts an event only when its UTC timestamp is at or after the matching `ProcessInstanceId.StartTimeUtc`; raw QPC and UTC values are never compared.

Read/write rates use attributed bytes observed during the actual UTC capture interval. Session totals begin when this Monitoring XS ETW source starts; they are not lifetime process counters. A healthy interval with no event is a real zero after the first `WarmingUp` capture. These metrics are labelled **Physical disk (ETW)** and remain distinct from **Process I/O**.

ETW access denied, session conflict, cancellation, loss, queue overflow, and unattributed events are explicit states. ETW loss or local queue overflow makes retained mapped values `Partial` lower bounds. Global unattributed traffic remains visible in diagnostics but does not incorrectly downgrade an otherwise complete mapped application. A correctly rejected pre-start event belongs to the old PID instance and likewise does not downgrade the current instance. When ETW reports lost events, the current event batch plus thread and IRP maps are discarded before further attribution to avoid stale-thread/PID contamination.

A future `Current I/O Share` is the application's share of attributed application disk I/O, not drive active time.

## Network (ETW)

Download and upload bytes come from typed kernel TCP/UDP send and receive events for IPv4 and IPv6. Monitoring XS reads the event PID and byte count but does not retain packet payloads, URLs, ports, or addresses. The Windows layer converts the ETW wall-clock timestamp to UTC before the collector compares it with `ProcessInstanceId.StartTimeUtc`.

Rates use bytes attributed during the actual UTC capture interval. Retained-session totals start with this Monitoring XS session and are not lifetime process counters. The first healthy capture is `WarmingUp`; a later healthy interval with no traffic is a real zero. Network remains separate from Process I/O and physical disk.

The network queue is bounded. ETW event loss or local queue overflow makes the affected interval `Partial`, and retained-session totals stay `Partial` lower bounds until the collector session resets. Access denied, unsupported platforms, same-name session conflicts, resource exhaustion, and collector errors remain explicit and never become zero.

Current TCP connection and UDP endpoint counts use bounded owner-PID IP Helper table snapshots. A count is shown only when both IPv4 and IPv6 tables for that protocol were read successfully; otherwise that count is unavailable.

## Services

Services and related background components are excluded by default. Advanced opt-in totals must be visibly labelled and must never silently replace the default calculation model.

## Retention target

- 0-10 minutes: about 1 second.
- 10-60 minutes: about 5 seconds.
- 1-6 hours: about 30 seconds.
- 6-24 hours: about 1 minute.

Downsampling preserves averages and peaks. Disk writes are batched in transactions; expired data is removed off the UI thread.
