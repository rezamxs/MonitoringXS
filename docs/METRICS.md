# Metric semantics

## Sampling

Live sampling defaults to one second. Each sample carries a monotonic capture interval and wall-clock timestamp. PID plus process start time identifies a process instance and prevents PID-reuse contamination.

## CPU

Per-process CPU is computed from the delta in total process CPU time divided by elapsed wall time and logical processor count. The Beginner value therefore represents percentage of total machine CPU capacity. The first sample is unavailable because no valid delta exists.

## Memory

Beginner Mode displays working set bytes. Advanced fields may add private working set, private bytes, commit, and peak working set when available. Aggregation sums current included process values.

## Process I/O

The current vertical slice calls Windows `GetProcessIoCounters` behind a platform abstraction. It calculates read/write bytes per second from cumulative counter deltas and retains cumulative read/write bytes and operation counts. These counters cover all I/O operations performed by a process, so the product labels them **Process I/O**, not disk activity. A first sample is `WarmingUp`; access-denied and failed reads remain unavailable.

If one process in a logical application cannot be sampled, an aggregate based on the remaining processes is marked `Partial` and displayed as a lower bound. It is never presented as a complete application total.

## Disk, network, and GPU

Physical-disk rates require a disk-specific attribution source such as ETW and are not yet implemented. A future `Current I/O Share` is the application's share of attributed application disk I/O, not drive active time. Network and GPU collectors must distinguish unavailable from zero and expose collection limitations without suppressing other metrics.

## Services

Services and related background components are excluded by default. Advanced opt-in totals must be visibly labelled and must never silently replace the default calculation model.

## Retention target

- 0-10 minutes: about 1 second.
- 10-60 minutes: about 5 seconds.
- 1-6 hours: about 30 seconds.
- 6-24 hours: about 1 minute.

Downsampling preserves averages and peaks. Disk writes are batched in transactions; expired data is removed off the UI thread.
