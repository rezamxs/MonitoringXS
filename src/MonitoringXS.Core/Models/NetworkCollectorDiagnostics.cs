namespace MonitoringXS.Core.Models;

public readonly record struct NetworkCollectorDiagnostics
{
    public NetworkAvailabilityReason Reason { get; init; }

    public long EtwEventsLost { get; init; }

    public long QueueEventsDropped { get; init; }

    public long UnattributedEvents { get; init; }

    public long PidReuseEventsRejected { get; init; }

    public long EventsObserved { get; init; }

    public long SendEvents { get; init; }

    public long ReceiveEvents { get; init; }

    public long TcpSendEvents { get; init; }

    public long TcpReceiveEvents { get; init; }

    public long UdpSendEvents { get; init; }

    public long UdpReceiveEvents { get; init; }

    public long IPv4Events { get; init; }

    public long IPv6Events { get; init; }

    public ulong TotalSourceSendBytes { get; init; }

    public ulong TotalSourceReceiveBytes { get; init; }

    public long AttributedEvents { get; init; }

    public long SystemProcessEvents { get; init; }

    public long OutsideApplicationSetEvents { get; init; }

    public long UnknownProcessEvents { get; init; }

    public long MetadataLookupFailures { get; init; }

    public long SessionStartFailures { get; init; }

    public long AccessDeniedFailures { get; init; }

    public long EventProcessingFailures { get; init; }

    public long UnsupportedEventVersions { get; init; }

    public double EventRatePerSecond { get; init; }

    public int CurrentQueueDepth { get; init; }

    public int MaximumQueueDepth { get; init; }

    public int QueueCapacity { get; init; }

    public int EtwBufferSizeMegabytes { get; init; }

    public double MaximumProcessingLatencyMilliseconds { get; init; }

    public double AverageProcessingLatencyMilliseconds { get; init; }

    public DateTimeOffset? LastSuccessfulEventTimestampUtc { get; init; }

    public MetricAvailability CollectorStatus { get; init; }

    public string? CollectorStatusReason { get; init; }

    public bool SessionTotalsAreLowerBounds { get; init; }
}
