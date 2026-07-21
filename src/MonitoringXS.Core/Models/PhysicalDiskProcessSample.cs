namespace MonitoringXS.Core.Models;

public sealed record PhysicalDiskProcessSample(
    ProcessInstanceId Process,
    DateTimeOffset CapturedAtUtc,
    MetricValue<double> ReadBytesPerSecond,
    MetricValue<double> WriteBytesPerSecond,
    MetricValue<ulong> SessionReadBytes,
    MetricValue<ulong> SessionWriteBytes,
    MetricValue<ulong> SessionReadOperationCount,
    MetricValue<ulong> SessionWriteOperationCount,
    PhysicalDiskCollectorDiagnostics Diagnostics);
