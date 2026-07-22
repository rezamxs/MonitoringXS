# ADR 0002: Use one bounded shared kernel metrics session

- Status: Accepted
- Date: 2026-07-22

## Context

Physical-disk and per-process network attribution both need Windows kernel ETW. Competing kernel sessions increase overhead and complicate ownership, shutdown, and loss reporting. ETW callbacks must not block while the UI or collector is busy.

## Decision

Monitoring XS owns one versioned session named `MonitoringXS.KernelMetrics.v1`. It enables only the disk, thread, and network keywords needed by current collectors. Disk and network events use separate bounded queues and separate drop counters.

Callbacks normalize timestamps and enqueue small typed events. Collectors compare UTC event time with the UTC process start time before accepting a PID. A same-name session is reported as unavailable instead of being restarted or replaced. Cancellation stops processing and disposal releases the session.

## Consequences

- Disk and network collection share session startup, access control, and shutdown.
- Queue overflow and ETW loss must remain visible and produce lower-bound results.
- The app continues with CPU, memory, and Process I/O when kernel ETW is unavailable.
- Any future kernel metric must justify its keyword, data retention, queue capacity, and privacy impact before joining this session.
