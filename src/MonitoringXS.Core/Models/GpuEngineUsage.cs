namespace MonitoringXS.Core.Models;

public readonly record struct GpuEngineUsage(
    GpuEngineId Engine,
    double UtilizationPercent);
