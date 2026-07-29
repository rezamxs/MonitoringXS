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
    private readonly IPhysicalDiskMetricCollector? _physicalDiskCollector;
    private readonly IPhysicalDiskAggregationService? _physicalDiskAggregation;
    private readonly INetworkMetricCollector? _networkCollector;
    private readonly INetworkMetricAggregationService? _networkAggregation;
    private readonly IGpuMetricCollector? _gpuCollector;
    private readonly IGpuMetricAggregationService? _gpuAggregation;
    private readonly IMetricHistoryStore? _historyStore;
    private readonly Dictionary<string, BoundedTimeSeries<ApplicationHistoryPoint>> _history = new(StringComparer.Ordinal);

    public MonitoringCoordinator(
        IProcessDiscoveryService discovery,
        IApplicationAttributionService attribution,
        IProcessMetricCollector collector,
        IMetricAggregationService aggregation,
        IPhysicalDiskMetricCollector? physicalDiskCollector = null,
        IPhysicalDiskAggregationService? physicalDiskAggregation = null,
        INetworkMetricCollector? networkCollector = null,
        INetworkMetricAggregationService? networkAggregation = null,
        IGpuMetricCollector? gpuCollector = null,
        IGpuMetricAggregationService? gpuAggregation = null,
        IMetricHistoryStore? historyStore = null)
    {
        _discovery = discovery;
        _attribution = attribution;
        _collector = collector;
        _aggregation = aggregation;
        _physicalDiskCollector = physicalDiskCollector;
        _physicalDiskAggregation = physicalDiskAggregation;
        _networkCollector = networkCollector;
        _networkAggregation = networkAggregation;
        _gpuCollector = gpuCollector;
        _gpuAggregation = gpuAggregation;
        _historyStore = historyStore;
    }

    public async ValueTask<MonitoringDashboardSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<ProcessDescriptor> processes = await _discovery.DiscoverAsync(cancellationToken);
        IReadOnlyList<AttributionResult> attribution = await _attribution.AttributeAsync(processes, cancellationToken);
        ProcessDescriptor[] attributedProcesses = attribution
            .Where(result => !result.IsHidden && result.Application is not null)
            .Select(result => result.Process)
            .ToArray();
        IReadOnlyList<ProcessMetricSample> metrics = await _collector.CollectAsync(
            attributedProcesses,
            capturedAt,
            cancellationToken);
        IReadOnlyList<ApplicationMetricSnapshot> applications = _aggregation.Aggregate(attribution, metrics, capturedAt);
        if (_physicalDiskCollector is not null && _physicalDiskAggregation is not null)
        {
            IReadOnlyList<PhysicalDiskProcessSample> physicalDiskSamples = await _physicalDiskCollector.CollectAsync(
                attributedProcesses,
                capturedAt,
                cancellationToken);
            IReadOnlyDictionary<string, PhysicalDiskMetricSet> physicalDiskByApplication =
                _physicalDiskAggregation.Aggregate(attribution, physicalDiskSamples);
            applications = applications
                .Select(application => physicalDiskByApplication.TryGetValue(
                    application.Application.LogicalApplicationId,
                    out PhysicalDiskMetricSet? physicalDisk)
                    ? application with { PhysicalDisk = physicalDisk }
                    : application)
                .ToArray();
        }

        if (_networkCollector is not null && _networkAggregation is not null)
        {
            IReadOnlyList<NetworkProcessSample> networkSamples = await _networkCollector.CollectAsync(
                attributedProcesses,
                capturedAt,
                cancellationToken);
            IReadOnlyDictionary<string, NetworkMetricSet> networkByApplication =
                _networkAggregation.Aggregate(attribution, networkSamples);
            applications = applications
                .Select(application => networkByApplication.TryGetValue(
                    application.Application.LogicalApplicationId,
                    out NetworkMetricSet? network)
                    ? application with { Network = network }
                    : application)
                .ToArray();
        }

        if (_gpuCollector is not null && _gpuAggregation is not null)
        {
            try
            {
                IReadOnlyList<GpuProcessSample> gpuSamples = await _gpuCollector.CollectAsync(
                    attributedProcesses,
                    capturedAt,
                    cancellationToken);
                IReadOnlyDictionary<string, GpuMetricSet> gpuByApplication =
                    _gpuAggregation.Aggregate(attribution, gpuSamples);
                applications = applications
                    .Select(application => gpuByApplication.TryGetValue(
                        application.Application.LogicalApplicationId,
                        out GpuMetricSet? gpu)
                        ? application with { Gpu = gpu }
                        : application)
                    .ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableGpuException(exception))
            {
                GpuMetricSet unavailable = GpuMetricSet.Unavailable(
                    MetricAvailability.Error,
                    GpuAvailabilityReason.CounterReadFailure,
                    "GPU collection failed; CPU, memory, Process I/O, Physical Disk, and Network remain available.");
                applications = applications
                    .Select(application => application with { Gpu = unavailable })
                    .ToArray();
            }
        }

        if (_historyStore is not null)
        {
            try
            {
                await _historyStore
                    .EnqueueAsync(applications, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException)
            {
                // History failure must never interrupt live metric collection.
            }
        }

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

    private static bool IsRecoverableGpuException(Exception exception) =>
        exception is ArgumentException
            or ArithmeticException
            or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException;
}
