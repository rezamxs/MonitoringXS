using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IProcessDiscoveryService
{
    ValueTask<IReadOnlyList<ProcessDescriptor>> DiscoverAsync(CancellationToken cancellationToken);
}
