namespace MonitoringXS.Core.Models;

public sealed record ProcessMetricSample(
    ProcessInstanceId Process,
    DateTimeOffset CapturedAt,
    MetricValue<double> CpuPercent,
    MetricValue<long> WorkingSetBytes,
    MetricValue<double> IoReadBytesPerSecond,
    MetricValue<double> IoWriteBytesPerSecond,
    MetricValue<ulong> TotalIoReadBytes,
    MetricValue<ulong> TotalIoWriteBytes,
    MetricValue<ulong> IoReadOperationCount,
    MetricValue<ulong> IoWriteOperationCount);
