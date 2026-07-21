using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IPhysicalDiskMetricCollector
{
    ValueTask<IReadOnlyList<PhysicalDiskProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken);
}
