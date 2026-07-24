namespace MonitoringXS.Core.Models;

public sealed record PhysicalDiskEventBatch(
    IReadOnlyList<PhysicalDiskIoEvent> Events,
    MetricAvailability Availability,
    long EtwEventsLost,
    long QueueEventsDropped,
    long UnattributedEvents,
    string? Detail = null,
    long EventsObserved = 0,
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
    DateTimeOffset? LastSuccessfulEventTimestampUtc = null);
