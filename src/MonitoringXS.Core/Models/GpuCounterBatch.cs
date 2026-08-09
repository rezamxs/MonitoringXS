namespace MonitoringXS.Core.Models;

public sealed record GpuCounterBatch(
    IReadOnlyList<GpuProcessCounterSnapshot> Processes,
    MetricAvailability Availability,
    GpuAvailabilityReason Reason,
    GpuCollectorDiagnostics Diagnostics)
{
    /// <summary>
    /// Machine-wide busiest GPU engine utilization across ALL processes on the system,
    /// not limited to monitored applications. Null when the counter source could not
    /// produce a valid machine-wide reading.
    /// </summary>
    public MetricValue<double>? MachineWideGpuUtilizationPercent { get; init; }
}
