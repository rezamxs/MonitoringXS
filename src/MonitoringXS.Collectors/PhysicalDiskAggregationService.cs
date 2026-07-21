using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class PhysicalDiskAggregationService : IPhysicalDiskAggregationService
{
    public IReadOnlyDictionary<string, PhysicalDiskMetricSet> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<PhysicalDiskProcessSample> metrics)
    {
        Dictionary<ProcessInstanceId, PhysicalDiskProcessSample> byProcess = metrics.ToDictionary(item => item.Process);
        return attribution
            .Where(item => !item.IsHidden && item.Application is not null)
            .GroupBy(item => item.Application!.LogicalApplicationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => AggregateApplication(group.Select(item => item.Process.InstanceId).ToArray(), byProcess),
                StringComparer.Ordinal);
    }

    private static PhysicalDiskMetricSet AggregateApplication(
        ProcessInstanceId[] processes,
        IReadOnlyDictionary<ProcessInstanceId, PhysicalDiskProcessSample> metrics)
    {
        PhysicalDiskProcessSample[] samples = processes
            .Select(process => metrics.GetValueOrDefault(process))
            .Where(sample => sample is not null)
            .Cast<PhysicalDiskProcessSample>()
            .ToArray();
        PhysicalDiskCollectorDiagnostics diagnostics = new(
            samples.Select(item => item.Diagnostics.EtwEventsLost).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.QueueEventsDropped).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.UnattributedEvents).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.PidReuseEventsRejected).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.EventsObserved).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.EventRatePerSecond).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.CurrentQueueDepth).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.MaximumQueueDepth).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.EtwBufferSizeMegabytes).DefaultIfEmpty().Max());

        return new PhysicalDiskMetricSet(
            SumDouble(samples.Select(item => item.ReadBytesPerSecond), processes.Length),
            SumDouble(samples.Select(item => item.WriteBytesPerSecond), processes.Length),
            SumUnsigned(samples.Select(item => item.SessionReadBytes), processes.Length),
            SumUnsigned(samples.Select(item => item.SessionWriteBytes), processes.Length),
            SumUnsigned(samples.Select(item => item.SessionReadOperationCount), processes.Length),
            SumUnsigned(samples.Select(item => item.SessionWriteOperationCount), processes.Length),
            diagnostics);
    }

    private static MetricValue<double> SumDouble(IEnumerable<MetricValue<double>> values, int expectedCount)
    {
        MetricValue<double>[] items = values.ToArray();
        double[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length == 0)
        {
            return Unavailable<double>(items.Select(item => item.Availability));
        }

        double sum = available.Sum();
        return available.Length == expectedCount && items.All(item => item.IsComplete)
            ? MetricValue<double>.Available(sum)
            : MetricValue<double>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
    }

    private static MetricValue<ulong> SumUnsigned(IEnumerable<MetricValue<ulong>> values, int expectedCount)
    {
        MetricValue<ulong>[] items = values.ToArray();
        ulong[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length == 0)
        {
            return Unavailable<ulong>(items.Select(item => item.Availability));
        }

        ulong sum = available.Aggregate(0UL, SaturatingAdd);
        return available.Length == expectedCount && items.All(item => item.IsComplete)
            ? MetricValue<ulong>.Available(sum)
            : MetricValue<ulong>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
    }

    private static MetricValue<T> Unavailable<T>(IEnumerable<MetricAvailability> values)
        where T : struct
    {
        MetricAvailability[] items = values.ToArray();
        MetricAvailability availability = items.FirstOrDefault(item => item != MetricAvailability.Available);
        return MetricValue<T>.Unavailable(
            availability == MetricAvailability.Available ? MetricAvailability.WarmingUp : availability);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static string PartialDetail(int complete, int total) =>
        $"Only {complete} of {total} physical-disk process samples were completely available; the displayed value is a lower bound.";
}
