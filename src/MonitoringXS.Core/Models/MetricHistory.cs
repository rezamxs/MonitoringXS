namespace MonitoringXS.Core.Models;

public enum MetricHistoryMetric
{
    CpuPercent,
    WorkingSetBytes,
    ProcessIoReadBytesPerSecond,
    ProcessIoWriteBytesPerSecond,
    PhysicalDiskReadBytesPerSecond,
    PhysicalDiskWriteBytesPerSecond,
    NetworkDownloadBytesPerSecond,
    NetworkUploadBytesPerSecond,
    GpuUtilizationPercent,
    GpuDedicatedMemoryBytes,
    GpuSharedMemoryBytes
}

public sealed record MetricHistoryPoint(
    string LogicalApplicationId,
    long? ApplicationSessionId,
    string? LegacyContinuityKey,
    DateTimeOffset TimestampUtc,
    MetricHistoryMetric Metric,
    double? Value,
    MetricAvailability Availability,
    string? Detail,
    bool IsDownsampled)
{
    public MetricHistoryPoint(
        string logicalApplicationId,
        string legacyContinuityKey,
        DateTimeOffset timestampUtc,
        MetricHistoryMetric metric,
        double? value,
        MetricAvailability availability,
        string? detail,
        bool isDownsampled)
        : this(
            logicalApplicationId,
            null,
            legacyContinuityKey,
            timestampUtc,
            metric,
            value,
            availability,
            detail,
            isDownsampled)
    {
    }

    public string ContinuityKey => ApplicationSessionId is long sessionId
        ? $"session:{sessionId}"
        : $"legacy:{LegacyContinuityKey}";
}

public sealed record MetricHistoryCapture(
    DateTimeOffset ObservedAtUtc,
    ProcessDiscoverySnapshot Discovery,
    IReadOnlyList<ApplicationMetricSnapshot> Applications);

public sealed record MetricHistoryWriteResult(
    bool Accepted,
    bool Dropped,
    string? Error = null)
{
    public static MetricHistoryWriteResult Success => new(true, false);

    public static MetricHistoryWriteResult DroppedResult(string reason) => new(false, true, reason);
}

public sealed record MetricHistoryQueryResult(
    IReadOnlyList<MetricHistoryPoint> Points,
    bool IsAvailable,
    string? Error = null);

public sealed record MetricHistoryApplication(
    string LogicalApplicationId,
    string DisplayName,
    ApplicationDisposition Disposition,
    DateTimeOffset UpdatedUtc);

public sealed record MetricHistoryApplicationsResult(
    IReadOnlyList<MetricHistoryApplication> Applications,
    bool IsAvailable,
    string? Error = null);

public sealed record MetricHistoryStoreDiagnostics(
    long BatchesEnqueued,
    long BatchesWritten,
    long SamplesWritten,
    long QueueDrops,
    long WriteFailures,
    int QueueDepth,
    long DatabaseBytes,
    long CleanupRuns,
    long LastCleanupMicroseconds,
    string? LastError);
