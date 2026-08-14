using System.Diagnostics;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application;

public enum MetricFamily
{
    PhysicalDisk,
    Network,
    Gpu
}

public sealed record MetricCaptureContext(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessDescriptor> Processes,
    IReadOnlyList<AttributionResult> Attribution);

public interface IMetricCaptureStage
{
    MetricFamily Family { get; }

    ValueTask<MetricCaptureContribution> CaptureAsync(
        MetricCaptureContext context,
        CancellationToken cancellationToken);

    MetricCaptureContribution Failed(Exception exception);
}

public abstract record MetricCaptureContribution(MetricFamily Family);

public sealed record PhysicalDiskMetricContribution(
    IReadOnlyDictionary<string, PhysicalDiskMetricSet> Metrics,
    PhysicalDiskCollectorDiagnostics? Diagnostics,
    PhysicalDiskMetricSet? Failure = null)
    : MetricCaptureContribution(MetricFamily.PhysicalDisk);

public sealed record NetworkMetricContribution(
    IReadOnlyDictionary<string, NetworkMetricSet> Metrics,
    NetworkCollectorDiagnostics? Diagnostics,
    NetworkMetricSet? Failure = null)
    : MetricCaptureContribution(MetricFamily.Network);

public sealed record GpuMetricContribution(
    IReadOnlyDictionary<string, GpuMetricSet> Metrics,
    GpuCounterBatch? LastBatch,
    GpuMetricSet? Failure = null)
    : MetricCaptureContribution(MetricFamily.Gpu);

public sealed record MonitoringMetricCaptureResult(
    IReadOnlyList<ApplicationMetricSnapshot> Applications,
    PhysicalDiskCollectorDiagnostics? PhysicalDiskDiagnostics,
    NetworkCollectorDiagnostics? NetworkDiagnostics,
    GpuCounterBatch? GpuBatch);

public sealed class MonitoringCapturePipeline
{
    private static readonly TimeSpan CollectorTimeout = TimeSpan.FromMilliseconds(750);
    private readonly IMetricCaptureStage[] _stages;

    public MonitoringCapturePipeline(IEnumerable<IMetricCaptureStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        _stages = stages.OrderBy(stage => stage.Family).ToArray();
        MetricFamily[] duplicates = _stages
            .GroupBy(stage => stage.Family)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate metric capture stages: {string.Join(", ", duplicates)}.");
        }
    }

    public async ValueTask<MonitoringMetricCaptureResult> CaptureAsync(
        MetricCaptureContext context,
        IReadOnlyList<ApplicationMetricSnapshot> baseApplications,
        CancellationToken cancellationToken)
    {
        Task<MetricCaptureContribution>[] captures = _stages
            .Select(stage => CaptureStageAsync(stage, context, cancellationToken))
            .ToArray();
        MetricCaptureContribution[] contributions = await Task.WhenAll(captures);

        IReadOnlyList<ApplicationMetricSnapshot> applications = baseApplications;
        PhysicalDiskCollectorDiagnostics? diskDiagnostics = null;
        NetworkCollectorDiagnostics? networkDiagnostics = null;
        GpuCounterBatch? gpuBatch = null;
        foreach (MetricCaptureContribution contribution in contributions)
        {
            switch (contribution)
            {
                case PhysicalDiskMetricContribution disk:
                    applications = applications.Select(application =>
                        disk.Metrics.TryGetValue(application.Application.LogicalApplicationId, out PhysicalDiskMetricSet? value)
                            ? application with { PhysicalDisk = value }
                            : disk.Failure is not null
                                ? application with { PhysicalDisk = disk.Failure }
                                : application).ToArray();
                    diskDiagnostics = disk.Diagnostics;
                    break;
                case NetworkMetricContribution network:
                    applications = applications.Select(application =>
                        network.Metrics.TryGetValue(application.Application.LogicalApplicationId, out NetworkMetricSet? value)
                            ? application with { Network = value }
                            : network.Failure is not null
                                ? application with { Network = network.Failure }
                                : application).ToArray();
                    networkDiagnostics = network.Diagnostics;
                    break;
                case GpuMetricContribution gpu:
                    applications = applications.Select(application =>
                        gpu.Metrics.TryGetValue(application.Application.LogicalApplicationId, out GpuMetricSet? value)
                            ? application with { Gpu = value }
                            : gpu.Failure is not null
                                ? application with { Gpu = gpu.Failure }
                                : application).ToArray();
                    gpuBatch = gpu.LastBatch;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported metric contribution {contribution.GetType().Name}.");
            }
        }

        return new(applications, diskDiagnostics, networkDiagnostics, gpuBatch);
    }

    private static async Task<MetricCaptureContribution> CaptureStageAsync(
        IMetricCaptureStage stage,
        MetricCaptureContext context,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CollectorTimeout);
        MetricCaptureContribution contribution;
        try
        {
            contribution = await stage.CaptureAsync(context, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceError(
                "Metric stage {0} failed ({1}).",
                stage.Family,
                exception.GetType().Name);
            return stage.Failed(exception);
        }

        if (contribution.Family != stage.Family)
        {
            throw new InvalidOperationException(
                $"Metric stage {stage.GetType().Name} returned {contribution.Family} instead of {stage.Family}.");
        }

        return contribution;
    }
}

public sealed class PhysicalDiskMetricStage(
    IPhysicalDiskMetricCollector collector,
    IPhysicalDiskAggregationService aggregation) : IMetricCaptureStage
{
    public MetricFamily Family => MetricFamily.PhysicalDisk;

    public async ValueTask<MetricCaptureContribution> CaptureAsync(
        MetricCaptureContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PhysicalDiskProcessSample> samples = await collector.CollectAsync(
            context.Processes,
            context.CapturedAt,
            cancellationToken);
        return new PhysicalDiskMetricContribution(
            aggregation.Aggregate(context.Attribution, samples),
            samples.Count > 0 ? samples[0].Diagnostics : null);
    }

    public MetricCaptureContribution Failed(Exception exception) => new PhysicalDiskMetricContribution(
        new Dictionary<string, PhysicalDiskMetricSet>(StringComparer.Ordinal),
        null,
        PhysicalDiskMetricSet.Unavailable(
            MetricAvailability.Error,
            "Physical Disk collection failed; other live metrics remain available."));
}

public sealed class NetworkMetricStage(
    INetworkMetricCollector collector,
    INetworkMetricAggregationService aggregation) : IMetricCaptureStage
{
    public MetricFamily Family => MetricFamily.Network;

    public async ValueTask<MetricCaptureContribution> CaptureAsync(
        MetricCaptureContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NetworkProcessSample> samples = await collector.CollectAsync(
            context.Processes,
            context.CapturedAt,
            cancellationToken);
        return new NetworkMetricContribution(
            aggregation.Aggregate(context.Attribution, samples),
            samples.Count > 0 ? samples[0].Diagnostics : null);
    }

    public MetricCaptureContribution Failed(Exception exception) => new NetworkMetricContribution(
        new Dictionary<string, NetworkMetricSet>(StringComparer.Ordinal),
        null,
        NetworkMetricSet.Unavailable(
            MetricAvailability.Error,
            NetworkAvailabilityReason.CollectorError,
            "Network collection failed; other live metrics remain available."));
}

public sealed class GpuMetricStage(
    IGpuMetricCollector collector,
    IGpuMetricAggregationService aggregation) : IMetricCaptureStage
{
    public MetricFamily Family => MetricFamily.Gpu;

    public async ValueTask<MetricCaptureContribution> CaptureAsync(
        MetricCaptureContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GpuProcessSample> samples = await collector.CollectAsync(
            context.Processes,
            context.CapturedAt,
            cancellationToken);
        return new GpuMetricContribution(
            aggregation.Aggregate(context.Attribution, samples),
            collector.LastBatch);
    }

    public MetricCaptureContribution Failed(Exception exception) => new GpuMetricContribution(
        new Dictionary<string, GpuMetricSet>(StringComparer.Ordinal),
        collector.LastBatch,
        GpuMetricSet.Unavailable(
            MetricAvailability.Error,
            GpuAvailabilityReason.CounterReadFailure,
            "GPU collection failed; CPU, memory, Process I/O, Physical Disk, and Network remain available."));
}
