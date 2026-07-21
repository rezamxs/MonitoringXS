using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IProcessResourceCounterReader
{
    MetricValue<ProcessResourceCounters> Read(ProcessInstanceId process);
}
