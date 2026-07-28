namespace MonitoringXS.Core.Models;

public sealed record GpuCounterBatch(
    IReadOnlyList<GpuProcessCounterSnapshot> Processes,
    MetricAvailability Availability,
    GpuAvailabilityReason Reason,
    GpuCollectorDiagnostics Diagnostics);
