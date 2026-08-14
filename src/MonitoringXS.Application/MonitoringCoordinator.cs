using System.Diagnostics;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Collections;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application;

public sealed class MonitoringCoordinator
{
    private const int OneMinuteCapacity = 60;
    private const int MaximumApplicationHistorySeries = 512;
    private readonly IProcessDiscoveryService _discovery;
    private readonly IApplicationAttributionService _attribution;
    private readonly IProcessMetricCollector _collector;
    private readonly IMetricAggregationService _aggregation;
    private readonly MonitoringCapturePipeline _pipeline;
    private readonly SystemOverviewService? _systemOverview;
    private readonly Dictionary<string, BoundedTimeSeries<ApplicationHistoryPoint>> _history = new(StringComparer.Ordinal);

    public MonitoringCoordinator(
        IProcessDiscoveryService discovery,
        IApplicationAttributionService attribution,
        IProcessMetricCollector collector,
        IMetricAggregationService aggregation,
        MonitoringCapturePipeline? pipeline = null,
        SystemOverviewService? systemOverview = null)
    {
        _discovery = discovery;
        _attribution = attribution;
        _collector = collector;
        _aggregation = aggregation;
        _pipeline = pipeline ?? new MonitoringCapturePipeline([]);
        _systemOverview = systemOverview;
    }

    public async ValueTask<MonitoringSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        ProcessDiscoverySnapshot discovery = await _discovery.DiscoverAsync(cancellationToken);
        IReadOnlyList<AttributionResult> attribution = await _attribution.AttributeAsync(
            discovery.Processes,
            cancellationToken);
        ProcessDescriptor[] attributedProcesses = attribution
            .Where(result => !result.IsHidden && result.Application is not null)
            .Select(result => result.Process)
            .ToArray();
        IReadOnlyList<ProcessMetricSample> metrics = await _collector.CollectAsync(
            attributedProcesses,
            capturedAt,
            cancellationToken);
        IReadOnlyList<ApplicationMetricSnapshot> baseApplications =
            _aggregation.Aggregate(attribution, metrics, capturedAt);
        MonitoringMetricCaptureResult metricCapture = await _pipeline.CaptureAsync(
            new(capturedAt, attributedProcesses, attribution),
            baseApplications,
            cancellationToken);
        ApplicationMetricSnapshot[] applications = metricCapture.Applications.ToArray();

        UpdateRecentHistory(applications, capturedAt);
        Dictionary<string, IReadOnlyList<ApplicationHistoryPoint>> historySnapshot = _history.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ApplicationHistoryPoint>)pair.Value.Snapshot().Select(item => item.Value).ToArray(),
            StringComparer.Ordinal);

        SystemOverviewSnapshot? systemOverview = null;
        IReadOnlyList<SystemOverviewHistoryPoint>? systemOverviewHistory = null;
        if (_systemOverview is not null)
        {
            try
            {
                systemOverview = await _systemOverview.CaptureAsync(
                    metricCapture.PhysicalDiskDiagnostics,
                    metricCapture.NetworkDiagnostics,
                    metricCapture.GpuBatch,
                    cancellationToken);
                systemOverviewHistory = _systemOverview.GetHistory();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceError(
                    "System overview capture failed ({0}).",
                    exception.GetType().Name);
            }
        }

        return new MonitoringSnapshot(
            capturedAt,
            discovery,
            applications,
            historySnapshot,
            systemOverview,
            systemOverviewHistory);
    }

    private void UpdateRecentHistory(
        IReadOnlyList<ApplicationMetricSnapshot> applications,
        DateTimeOffset capturedAt)
    {
        HashSet<string> activeIds = applications
            .Select(application => application.Application.LogicalApplicationId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string staleId in _history.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _history.Remove(staleId);
        }

        foreach (ApplicationMetricSnapshot application in applications)
        {
            if (!_history.TryGetValue(application.Application.LogicalApplicationId, out BoundedTimeSeries<ApplicationHistoryPoint>? series))
            {
                if (_history.Count >= MaximumApplicationHistorySeries)
                {
                    continue;
                }

                series = new BoundedTimeSeries<ApplicationHistoryPoint>(OneMinuteCapacity);
                _history.Add(application.Application.LogicalApplicationId, series);
            }

            series.Add(capturedAt, new(
                capturedAt,
                application.CpuPercent.IsAvailable ? application.CpuPercent.Value : null,
                application.WorkingSetBytes.IsAvailable ? application.WorkingSetBytes.Value : null));
        }
    }
}
