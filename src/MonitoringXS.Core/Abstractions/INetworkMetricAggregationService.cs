using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface INetworkMetricAggregationService
{
    IReadOnlyDictionary<string, NetworkMetricSet> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<NetworkProcessSample> metrics);
}
