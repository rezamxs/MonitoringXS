using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IMetricAggregationService
{
    IReadOnlyList<ApplicationMetricSnapshot> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<ProcessMetricSample> metrics,
        DateTimeOffset capturedAt);
}
