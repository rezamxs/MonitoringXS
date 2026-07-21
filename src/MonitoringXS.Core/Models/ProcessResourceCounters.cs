namespace MonitoringXS.Core.Models;

public readonly record struct ProcessResourceCounters(
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    MetricValue<ProcessIoCounters> IoCounters);
