namespace MonitoringXS.Core.Models;

public sealed record ApplicationMetricSnapshot(
    ApplicationIdentity Application,
    DateTimeOffset CapturedAt,
    MetricValue<double> CpuPercent,
    MetricValue<long> WorkingSetBytes,
    MetricValue<double> IoReadBytesPerSecond,
    MetricValue<double> IoWriteBytesPerSecond,
    MetricValue<ulong> TotalIoReadBytes,
    MetricValue<ulong> TotalIoWriteBytes,
    MetricValue<ulong> IoReadOperationCount,
    MetricValue<ulong> IoWriteOperationCount,
    int ProcessCount,
    IReadOnlyList<ProcessDescriptor> Processes)
{
    public PhysicalDiskMetricSet PhysicalDisk { get; init; } = PhysicalDiskMetricSet.Unavailable(
        MetricAvailability.Unsupported,
        "Physical-disk attribution is not configured.");

    public NetworkMetricSet Network { get; init; } = NetworkMetricSet.Unavailable(
        MetricAvailability.Unsupported,
        NetworkAvailabilityReason.Unsupported,
        "Network attribution is not configured.");

    public GpuMetricSet Gpu { get; init; } = GpuMetricSet.Unavailable(
        MetricAvailability.Unsupported,
        GpuAvailabilityReason.CounterSetUnavailable,
        "GPU attribution is not configured.");
}
