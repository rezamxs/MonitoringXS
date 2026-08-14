using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IProcessDiscoveryService
{
    ValueTask<ProcessDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken);
}
