namespace MonitoringXS.Core.Models;

public sealed record GpuMetricSet(
    MetricValue<double> UtilizationPercent,
    MetricValue<ulong> DedicatedMemoryBytes,
    MetricValue<ulong> SharedMemoryBytes,
    GpuEngineId? BusiestEngine,
    GpuCollectorDiagnostics Diagnostics)
{
    public MetricAvailability Availability => UtilizationPercent.Availability;

    public GpuAvailabilityReason Reason
    {
        get
        {
            if (Diagnostics.Reason != GpuAvailabilityReason.None)
            {
                return Diagnostics.Reason;
            }

            MetricAvailability[] states =
            [
                UtilizationPercent.Availability,
                DedicatedMemoryBytes.Availability,
                SharedMemoryBytes.Availability
            ];
            return states.Contains(MetricAvailability.AccessDenied)
                ? GpuAvailabilityReason.AccessDenied
                : states.Contains(MetricAvailability.Unsupported)
                    ? GpuAvailabilityReason.CounterSetUnavailable
                    : states.Contains(MetricAvailability.Error)
                        ? GpuAvailabilityReason.InvalidData
                        : states.Contains(MetricAvailability.Unavailable)
                            ? GpuAvailabilityReason.CounterUnavailable
                            : states.Contains(MetricAvailability.WarmingUp)
                                ? GpuAvailabilityReason.WarmingUp
                                : states.Contains(MetricAvailability.Partial)
                                    ? GpuAvailabilityReason.InvalidData
                                    : GpuAvailabilityReason.None;
        }
    }

    public static GpuMetricSet Unavailable(
        MetricAvailability availability,
        GpuAvailabilityReason reason,
        string? detail = null) => new(
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        null,
        new GpuCollectorDiagnostics
        {
            ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
            CollectorStatus = availability,
            Reason = reason,
            CollectorStatusReason = detail,
            SharedMemoryMayDoubleCountAcrossProcesses = true
        });
}
