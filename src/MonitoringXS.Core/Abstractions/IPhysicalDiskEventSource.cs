using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IPhysicalDiskEventSource
{
    ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(CancellationToken cancellationToken);
}
