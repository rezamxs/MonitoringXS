namespace MonitoringXS.Core.Models;

public sealed record NetworkEventBatch(
    IReadOnlyList<NetworkTrafficEvent> Events,
    MetricAvailability Availability,
    NetworkAvailabilityReason Reason,
    long EtwEventsLost,
    long QueueEventsDropped,
    long UnattributedEvents,
    string? Detail = null,
    long EventsObserved = 0,
    int CurrentQueueDepth = 0,
    int MaximumQueueDepth = 0,
    int EtwBufferSizeMegabytes = 0,
    IReadOnlyDictionary<int, int>? ActiveTcpConnectionsByProcess = null,
    IReadOnlyDictionary<int, int>? UdpEndpointsByProcess = null);
