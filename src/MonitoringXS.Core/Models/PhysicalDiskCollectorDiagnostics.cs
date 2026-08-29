namespace MonitoringXS.Core.Models;

public readonly record struct PhysicalDiskCollectorDiagnostics(
    long EtwEventsLost,
    long QueueEventsDropped,
    long UnattributedEvents,
    long PidReuseEventsRejected,
    long EventsObserved = 0,
    double EventRatePerSecond = 0,
    int CurrentQueueDepth = 0,
    int MaximumQueueDepth = 0,
    int EtwBufferSizeMegabytes = 0,
    long ReadEventsObserved = 0,
    long WriteEventsObserved = 0,
    ulong ReadBytesObserved = 0,
    ulong WriteBytesObserved = 0,
    long MetadataLookupFailures = 0,
    long SessionStartFailures = 0,
    long AccessDeniedFailures = 0,
    double ProcessingLatencyMilliseconds = 0,
    DateTimeOffset? LastSuccessfulEventTimestampUtc = null,
    MetricAvailability? CollectorStatus = null,
    bool SessionTotalsAreLowerBounds = false);
