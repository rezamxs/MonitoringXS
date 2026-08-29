using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface INetworkEventSource
{
    ValueTask<NetworkEventBatch> ReadNetworkBatchAsync(
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken);
}
