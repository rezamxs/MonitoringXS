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
    string ProcessLifetimeKey,
    DateTimeOffset TimestampUtc,
    MetricHistoryMetric Metric,
    double? Value,
    MetricAvailability Availability,
    string? Detail,
    bool IsDownsampled);

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
