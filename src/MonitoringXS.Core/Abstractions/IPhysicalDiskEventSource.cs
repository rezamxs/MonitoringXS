using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IPhysicalDiskEventSource
{
    ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken);
}
