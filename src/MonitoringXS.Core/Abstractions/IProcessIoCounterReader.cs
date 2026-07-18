using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IProcessIoCounterReader
{
    MetricValue<ProcessIoCounters> Read(ProcessInstanceId process);
}
