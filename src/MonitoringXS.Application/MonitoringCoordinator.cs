using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Collections;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application;

public sealed class MonitoringCoordinator
{
    private const int OneMinuteCapacity = 60;
    private readonly IProcessDiscoveryService _discovery;
    private readonly IApplicationAttributionService _attribution;
    private readonly IProcessMetricCollector _collector;
    private readonly IMetricAggregationService _aggregation;
    private readonly Dictionary<string, BoundedTimeSeries<ApplicationHistoryPoint>> _history = new(StringComparer.Ordinal);

    public MonitoringCoordinator(
        IProcessDiscoveryService discovery,
        IApplicationAttributionService attribution,
        IProcessMetricCollector collector,
        IMetricAggregationService aggregation)
    {
        _discovery = discovery;
        _attribution = attribution;
        _collector = collector;
        _aggregation = aggregation;
    }

    public async ValueTask<MonitoringDashboardSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<ProcessDescriptor> processes = await _discovery.DiscoverAsync(cancellationToken);
        IReadOnlyList<AttributionResult> attribution = _attribution.Attribute(processes);
        IReadOnlyList<ProcessMetricSample> metrics = await _collector.CollectAsync(processes, capturedAt, cancellationToken);
        IReadOnlyList<ApplicationMetricSnapshot> applications = _aggregation.Aggregate(attribution, metrics, capturedAt);

        foreach (ApplicationMetricSnapshot application in applications)
        {
            if (!_history.TryGetValue(application.Application.LogicalApplicationId, out BoundedTimeSeries<ApplicationHistoryPoint>? series))
            {
                series = new BoundedTimeSeries<ApplicationHistoryPoint>(OneMinuteCapacity);
                _history.Add(application.Application.LogicalApplicationId, series);
            }

            ApplicationHistoryPoint point = new(
                capturedAt,
                application.CpuPercent.IsAvailable ? application.CpuPercent.Value : null,
                application.WorkingSetBytes.IsAvailable ? application.WorkingSetBytes.Value : null);
            series.Add(capturedAt, point);
        }

        Dictionary<string, IReadOnlyList<ApplicationHistoryPoint>> historySnapshot = _history.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ApplicationHistoryPoint>)pair.Value.Snapshot().Select(item => item.Value).ToArray(),
            StringComparer.Ordinal);

        return new MonitoringDashboardSnapshot(
            capturedAt,
            applications.Where(item => item.Application.Disposition is ApplicationDisposition.Installed or ApplicationDisposition.Packaged).ToArray(),
            applications.Where(item => item.Application.Disposition is ApplicationDisposition.Portable or ApplicationDisposition.Unresolved).ToArray(),
            historySnapshot);
    }
}
