namespace MonitoringXS.Core.Models;

/// <summary>
/// Immutable system-wide resource snapshot. These are machine-level values,
/// not sums of logical application cards unless that is mathematically and semantically correct.
/// </summary>
public sealed record SystemOverviewSnapshot(
    DateTimeOffset CapturedAt,
    MetricValue<double> TotalCpuPercent,
    MetricValue<long> TotalPhysicalMemoryBytes,
    MetricValue<long> UsedPhysicalMemoryBytes,
    MetricValue<long> AvailablePhysicalMemoryBytes,
    MetricValue<double> PhysicalMemoryUtilizationPercent,
    MetricValue<double> DiskReadBytesPerSecond,
    MetricValue<double> DiskWriteBytesPerSecond,
    MetricValue<double> NetworkReceiveBytesPerSecond,
    MetricValue<double> NetworkSendBytesPerSecond,
    MetricValue<double> GpuUtilizationPercent,
    SystemOverviewDiagnostics Diagnostics);