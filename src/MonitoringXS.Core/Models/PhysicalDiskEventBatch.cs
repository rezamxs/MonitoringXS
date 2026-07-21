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
    int EtwBufferSizeMegabytes = 0);
