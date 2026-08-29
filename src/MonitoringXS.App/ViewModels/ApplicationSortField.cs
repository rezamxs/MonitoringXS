namespace MonitoringXS.App.ViewModels;

public enum ApplicationSortField
{
    ApplicationName,
    ProcessId,
    CpuUsage,
    MemoryUsage,
    ProcessIoRate,
    PhysicalDiskRate,
    NetworkRate,
    GpuUsage
}

public sealed record ApplicationSortOption(ApplicationSortField Field, string Label);
