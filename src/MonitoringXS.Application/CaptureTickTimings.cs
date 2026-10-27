namespace MonitoringXS.Application;

/// <summary>
/// Allocation-light wall-clock timings for one capture tick, measured at the
/// existing stage boundaries with <see cref="System.Diagnostics.Stopwatch"/>. A
/// null duration means the stage did not run or was not measured; it is never
/// reported as zero.
/// </summary>
public sealed record CaptureTickTimings
{
    public TimeSpan? Total { get; init; }

    public TimeSpan? Capture { get; init; }

    public TimeSpan? Discovery { get; init; }

    public TimeSpan? Attribution { get; init; }

    public TimeSpan? ProcessCollection { get; init; }

    public TimeSpan? Aggregation { get; init; }

    public TimeSpan? MetricPipeline { get; init; }

    public TimeSpan? PhysicalDiskStage { get; init; }

    public TimeSpan? NetworkStage { get; init; }

    public TimeSpan? GpuStage { get; init; }

    public TimeSpan? SystemOverview { get; init; }

    public TimeSpan? HistoryEnqueue { get; init; }

    public TimeSpan? SnapshotPublish { get; init; }
}
