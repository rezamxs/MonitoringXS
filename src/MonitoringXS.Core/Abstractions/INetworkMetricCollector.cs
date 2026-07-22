using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface INetworkMetricCollector
{
    ValueTask<IReadOnlyList<NetworkProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken);
}
