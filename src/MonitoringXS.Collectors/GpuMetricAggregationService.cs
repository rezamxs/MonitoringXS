using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class GpuMetricAggregationService : IGpuMetricAggregationService
{
    public IReadOnlyDictionary<string, GpuMetricSet> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<GpuProcessSample> metrics)
    {
        Dictionary<ProcessInstanceId, GpuProcessSample> byProcess =
            metrics
                .GroupBy(sample => sample.Process)
                .ToDictionary(group => group.Key, group => group.First());
        return attribution
            .Where(result => !result.IsHidden && result.Application is not null)
            .GroupBy(result => result.Application!.LogicalApplicationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => AggregateApplication(
                    group.Select(result => result.Process.InstanceId).Distinct().ToArray(),
                    byProcess),
                StringComparer.Ordinal);
    }

    private static GpuMetricSet AggregateApplication(
        ProcessInstanceId[] processes,
        IReadOnlyDictionary<ProcessInstanceId, GpuProcessSample> metrics)
    {
        GpuProcessSample[] samples = processes
            .Select(process => metrics.GetValueOrDefault(process))
            .Where(sample => sample is not null)
            .Cast<GpuProcessSample>()
            .ToArray();
        GpuCollectorDiagnostics diagnostics = samples
            .Select(sample => sample.Diagnostics)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(diagnostics.ProviderName))
        {
            diagnostics = new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Unavailable,
                Reason = GpuAvailabilityReason.ProcessUnavailable,
                CollectorStatusReason = "No GPU process sample matched this logical application.",
                UtilizationCounterStatus = MetricAvailability.Unavailable,
                DedicatedMemoryCounterStatus = MetricAvailability.Unavailable,
                SharedMemoryCounterStatus = MetricAvailability.Unavailable,
                SharedMemoryMayDoubleCountAcrossProcesses = true
            };
        }

        (MetricValue<double> utilization, GpuEngineId? busiestEngine) =
            AggregateUtilization(samples, processes.Length);
        return new GpuMetricSet(
            utilization,
            SumUnsigned(
                samples.Select(sample => sample.DedicatedMemoryBytes),
                processes.Length,
                "dedicated GPU memory"),
            SumUnsigned(
                samples.Select(sample => sample.SharedMemoryBytes),
                processes.Length,
                "shared GPU memory"),
            busiestEngine,
            diagnostics);
    }

    private static (MetricValue<double> Utilization, GpuEngineId? BusiestEngine) AggregateUtilization(
        GpuProcessSample[] samples,
        int expectedCount)
    {
        GpuProcessSample[] available = samples
            .Where(sample => sample.UtilizationPercent.IsAvailable)
            .ToArray();
        if (available.Length == 0)
        {
            MetricValue<double>[] unavailable = samples
                .Select(sample => sample.UtilizationPercent)
                .ToArray();
            return (
                PreserveUnavailableDetail(unavailable),
                null);
        }

        bool duplicateEngineIdentity = available.Any(sample =>
            sample.Engines.GroupBy(engine => engine.Engine).Any(group => group.Count() > 1));
        bool invalidEngineValue = available.Any(sample =>
            sample.Engines.Any(engine =>
                !double.IsFinite(engine.UtilizationPercent)
                || engine.UtilizationPercent < 0d
                || engine.UtilizationPercent > 100d));
        GpuEngineUsage[] normalized = available
            .SelectMany(sample => sample.Engines
                .Where(engine => double.IsFinite(engine.UtilizationPercent)
                    && engine.UtilizationPercent >= 0d
                    && engine.UtilizationPercent <= 100d)
                .GroupBy(engine => engine.Engine)
                .Select(group => new GpuEngineUsage(
                    group.Key,
                    group.Max(engine => engine.UtilizationPercent))))
            .ToArray();
        (GpuEngineId Engine, double Utilization, bool ExceededCapacity)[] engines = normalized
            .GroupBy(engine => engine.Engine)
            .Select(group =>
            {
                double sum = group.Sum(engine => engine.UtilizationPercent);
                return (
                    group.Key,
                    Math.Min(sum, 100d),
                    sum > 100d);
            })
            .OrderByDescending(engine => engine.Item2)
            .ToArray();
        double value = engines.Select(engine => engine.Utilization).DefaultIfEmpty().Max();
        bool exceededCapacity = engines.Any(engine => engine.ExceededCapacity);
        if (exceededCapacity)
        {
            return (
                MetricValue<double>.Unavailable(
                    MetricAvailability.Error,
                    "The combined value for one GPU engine exceeded 100%; the interval was rejected."),
                engines.Length == 0 ? null : engines[0].Engine);
        }

        bool complete = available.Length == expectedCount
            && samples.Length == expectedCount
            && samples.All(sample => sample.UtilizationPercent.IsComplete)
            && !duplicateEngineIdentity
            && !invalidEngineValue;
        MetricValue<double> metric = complete
            ? MetricValue<double>.Available(value)
            : MetricValue<double>.Partial(
                value,
                duplicateEngineIdentity
                        ? "A duplicate GPU engine instance was ignored; utilization may be incomplete."
                        : invalidEngineValue
                            ? "An invalid GPU engine value was ignored; utilization is a lower bound."
                            : $"Only {available.Length} of {expectedCount} GPU process samples were available; utilization is a lower bound.");
        return (metric, engines.Length == 0 ? null : engines[0].Engine);
    }

    private static MetricValue<ulong> SumUnsigned(
        IEnumerable<MetricValue<ulong>> values,
        int expectedCount,
        string name)
    {
        MetricValue<ulong>[] items = values.ToArray();
        ulong[] available = items
            .Where(item => item.IsAvailable)
            .Select(item => item.Value!.Value)
            .ToArray();
        if (available.Length == 0)
        {
            return PreserveUnavailableDetail(items);
        }

        ulong sum = 0;
        bool overflow = false;
        foreach (ulong value in available)
        {
            if (ulong.MaxValue - sum < value)
            {
                sum = ulong.MaxValue;
                overflow = true;
                break;
            }

            sum += value;
        }

        return available.Length == expectedCount
            && items.Length == expectedCount
            && items.All(item => item.IsComplete)
            && !overflow
            ? MetricValue<ulong>.Available(sum)
            : MetricValue<ulong>.Partial(
                sum,
                overflow
                    ? $"The {name} sum overflowed the supported range; the displayed value is a lower bound."
                    : $"Only {available.Length} of {expectedCount} process values contributed to {name}; the sum of reported process values is a lower bound.");
    }

    private static MetricAvailability SelectUnavailable(
        IEnumerable<MetricAvailability> values)
    {
        MetricAvailability[] items = values.ToArray();
        MetricAvailability[] priority =
        [
            MetricAvailability.AccessDenied,
            MetricAvailability.Unsupported,
            MetricAvailability.Error,
            MetricAvailability.WarmingUp,
            MetricAvailability.Unavailable
        ];
        return priority.FirstOrDefault(items.Contains, MetricAvailability.Unavailable);
    }

    private static MetricValue<T> PreserveUnavailableDetail<T>(
        IReadOnlyList<MetricValue<T>> values)
        where T : struct
    {
        MetricAvailability availability = SelectUnavailable(
            values.Select(value => value.Availability));
        string? detail = values
            .Where(value => value.Availability == availability)
            .Select(value => value.Detail)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? values
                .Select(value => value.Detail)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return MetricValue<T>.Unavailable(availability, detail);
    }
}
