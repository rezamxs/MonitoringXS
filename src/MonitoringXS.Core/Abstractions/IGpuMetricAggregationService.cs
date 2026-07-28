using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IGpuMetricAggregationService
{
    IReadOnlyDictionary<string, GpuMetricSet> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<GpuProcessSample> metrics);
}
