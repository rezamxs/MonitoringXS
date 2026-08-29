namespace MonitoringXS.Core.Models;

public sealed record GpuProcessCounterSnapshot(
    ProcessInstanceId Process,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<GpuEngineUsage> Engines,
    MetricValue<ulong> DedicatedMemoryBytes,
    MetricValue<ulong> SharedMemoryBytes,
    MetricAvailability EngineAvailability,
    string? EngineDetail = null);
