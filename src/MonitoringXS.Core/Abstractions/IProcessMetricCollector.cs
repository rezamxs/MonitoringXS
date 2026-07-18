using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IProcessMetricCollector
{
    ValueTask<IReadOnlyList<ProcessMetricSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken);
}
