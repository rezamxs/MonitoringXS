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
    int EtwBufferSizeMegabytes = 0);
