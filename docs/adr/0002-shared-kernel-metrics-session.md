# ADR 0002: Use one bounded shared kernel metrics session

- Status: Accepted
- Date: 2026-07-22

## Context

Physical-disk and per-process network attribution both need Windows kernel ETW. Competing kernel sessions increase overhead and complicate ownership, shutdown, and loss reporting. ETW callbacks must not block while the UI or collector is busy.

## Decision

Monitoring XS owns one versioned session named `MonitoringXS.KernelMetrics.v1`. It enables only the disk, thread, and network keywords needed by current collectors. Disk and network events use separate bounded queues and separate drop counters.

Callbacks normalize timestamps and enqueue small typed events. Collectors compare UTC event time with the UTC process start time before accepting a PID. Network callback failures that can result from malformed event data are counted and isolated so they do not end physical-disk collection. Network and physical-disk availability are reported separately above the shared session-start boundary. A same-name session is reported as unavailable instead of being restarted or replaced. Cancellation stops processing and disposal releases the session.

Physical-disk and network sample timestamps remain UTC, but rate denominators use monotonic elapsed time so system-clock adjustments cannot create a negative rate or spike. Confirmed ETW loss, queue overflow, parsing failure, unsupported event version, or collector restart keeps affected network session totals marked as lower bounds.

Disk-init events establish the issuing PID for a bounded IRP entry. Because one IRP can produce multiple split disk completions, reading a completion does not immediately remove that entry. A later PID 4 split-init cannot overwrite an established user initiator; a later user init replaces it, and normal capacity eviction keeps stale state bounded. Disk completions that still resolve to PID 4 or a process outside the current application set remain unattributed instead of being guessed.

## Consequences

- Disk and network collection share session startup, access control, and shutdown.
- Queue overflow and ETW loss must remain visible and produce lower-bound results.
- The app continues with CPU, memory, and Process I/O when kernel ETW is unavailable.
- Any future kernel metric must justify its keyword, data retention, queue capacity, and privacy impact before joining this session.
