namespace MonitoringXS.Core.Models;

/// <summary>
/// User-meaningful unit of a metric value. Never store UI-formatted text as the
/// canonical representation; presentation layers format the enum.
/// </summary>
public enum MetricUnit
{
    Percent,
    Bytes,
    BytesPerSecond,
    Count,
    Time,
    Unknown
}

/// <summary>
/// Scope a metric value describes. Logical-application metrics must never be
/// described as process metrics.
/// </summary>
public enum MetricScope
{
    System,
    LogicalApplication,
    Process,
    Collector
}

/// <summary>
/// User-understandable origin of a metric. Frames the source as the user sees it;
/// precise backend terminology stays in diagnostics areas.
/// </summary>
public enum MetricSource
{
    WindowsPerformanceCounters,
    OperatingSystem,
    AdvancedMonitoringEngine,
    Calculated,
    LocalHistory,
    Unknown
}

/// <summary>
/// How a metric value is produced over time. Only claim semantics the collector
/// actually implements.
/// </summary>
public enum MetricSamplingKind
{
    CurrentSnapshot,
    PeriodicallySampled,
    AggregatedOverInterval,
    CumulativeSinceStart,
    Unknown
}

/// <summary>
/// Lightweight typed metadata describing one user-facing metric family.
/// Presentation (explanations, tooltips, diagnostics) reads this instead of
/// re-inventing per-metric strings.
/// </summary>
public sealed record MetricMetadata(
    MetricUnit Unit,
    MetricScope Scope,
    MetricSource Source,
    MetricSamplingKind Sampling)
{
    public static MetricMetadata Unknown { get; } = new(
        MetricUnit.Unknown,
        MetricScope.Collector,
        MetricSource.Unknown,
        MetricSamplingKind.Unknown);
}
