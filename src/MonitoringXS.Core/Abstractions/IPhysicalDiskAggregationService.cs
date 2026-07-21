using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IPhysicalDiskAggregationService
{
    IReadOnlyDictionary<string, PhysicalDiskMetricSet> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<PhysicalDiskProcessSample> metrics);
}
