namespace MonitoringXS.Core.Models;

/// <summary>
/// Bounded diagnostics for system-wide overview metrics.
/// All counters are non-negative and represent cumulative or instantaneous state.
/// </summary>
public sealed record SystemOverviewDiagnostics(
    MetricAvailability CpuAvailability,
    string? CpuDetail,
    MetricAvailability MemoryAvailability,
    string? MemoryDetail,
    MetricAvailability DiskAvailability,
    string? DiskDetail,
    MetricAvailability NetworkAvailability,
    string? NetworkDetail,
    MetricAvailability GpuAvailability,
    string? GpuDetail,
    int CaptureDurationMilliseconds,
    bool IsComplete)
{
    public static SystemOverviewDiagnostics Empty { get; } = new(
        MetricAvailability.WarmingUp,
        "No system overview capture has been performed.",
        MetricAvailability.WarmingUp,
        null,
        MetricAvailability.WarmingUp,
        null,
        MetricAvailability.WarmingUp,
        null,
        MetricAvailability.WarmingUp,
        null,
        0,
        false);
}