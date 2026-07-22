namespace MonitoringXS.Core.Models;

public readonly record struct NetworkCollectorDiagnostics(
    NetworkAvailabilityReason Reason,
    long EtwEventsLost,
    long QueueEventsDropped,
    long UnattributedEvents,
    long PidReuseEventsRejected,
    long EventsObserved,
    double EventRatePerSecond,
    int CurrentQueueDepth,
    int MaximumQueueDepth,
    int EtwBufferSizeMegabytes,
    bool SessionTotalsAreLowerBounds);
