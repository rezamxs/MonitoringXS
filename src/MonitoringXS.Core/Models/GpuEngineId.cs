namespace MonitoringXS.Core.Models;

public readonly record struct GpuEngineId(
    ulong AdapterLuid,
    int PhysicalAdapterIndex,
    int EngineIndex,
    string EngineType);
