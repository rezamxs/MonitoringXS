using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface INetworkEventSource
{
    ValueTask<NetworkEventBatch> ReadNetworkBatchAsync(CancellationToken cancellationToken);
}
