namespace MonitoringXS.App.ViewModels;

public enum ApplicationSortField
{
    ApplicationName,
    CpuUsage,
    MemoryUsage,
    ProcessIoRate,
    PhysicalDiskRate,
    NetworkRate,
    GpuUsage,
    ProcessCount
}

public sealed record ApplicationSortOption(ApplicationSortField Field, string Label);
