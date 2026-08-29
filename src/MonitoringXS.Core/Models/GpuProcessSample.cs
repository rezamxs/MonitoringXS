namespace MonitoringXS.Core.Models;

public sealed record GpuProcessSample(
    ProcessInstanceId Process,
    DateTimeOffset CapturedAtUtc,
    MetricValue<double> UtilizationPercent,
    MetricValue<ulong> DedicatedMemoryBytes,
    MetricValue<ulong> SharedMemoryBytes,
    IReadOnlyList<GpuEngineUsage> Engines,
    GpuCollectorDiagnostics Diagnostics);
