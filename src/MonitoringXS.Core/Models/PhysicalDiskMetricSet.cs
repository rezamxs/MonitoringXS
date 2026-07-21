namespace MonitoringXS.Core.Models;

public sealed record PhysicalDiskMetricSet(
    MetricValue<double> ReadBytesPerSecond,
    MetricValue<double> WriteBytesPerSecond,
    MetricValue<ulong> SessionReadBytes,
    MetricValue<ulong> SessionWriteBytes,
    MetricValue<ulong> SessionReadOperationCount,
    MetricValue<ulong> SessionWriteOperationCount,
    PhysicalDiskCollectorDiagnostics Diagnostics)
{
    public static PhysicalDiskMetricSet Unavailable(MetricAvailability availability, string? detail = null) => new(
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        default);
}
