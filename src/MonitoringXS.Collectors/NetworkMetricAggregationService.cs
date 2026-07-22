using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class NetworkMetricAggregationService : INetworkMetricAggregationService
{
    public IReadOnlyDictionary<string, NetworkMetricSet> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<NetworkProcessSample> metrics)
    {
        Dictionary<ProcessInstanceId, NetworkProcessSample> byProcess = metrics.ToDictionary(item => item.Process);
        return attribution
            .Where(item => !item.IsHidden && item.Application is not null)
            .GroupBy(item => item.Application!.LogicalApplicationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => AggregateApplication(group.Select(item => item.Process.InstanceId).ToArray(), byProcess),
                StringComparer.Ordinal);
    }

    private static NetworkMetricSet AggregateApplication(
        ProcessInstanceId[] processes,
        IReadOnlyDictionary<ProcessInstanceId, NetworkProcessSample> metrics)
    {
        NetworkProcessSample[] samples = processes
            .Select(process => metrics.GetValueOrDefault(process))
            .Where(sample => sample is not null)
            .Cast<NetworkProcessSample>()
            .ToArray();
        NetworkCollectorDiagnostics diagnostics = new(
            samples.Select(item => item.Diagnostics.Reason).FirstOrDefault(item => item != NetworkAvailabilityReason.None),
            samples.Select(item => item.Diagnostics.EtwEventsLost).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.QueueEventsDropped).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.UnattributedEvents).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.PidReuseEventsRejected).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.EventsObserved).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.EventRatePerSecond).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.CurrentQueueDepth).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.MaximumQueueDepth).DefaultIfEmpty().Max(),
            samples.Select(item => item.Diagnostics.EtwBufferSizeMegabytes).DefaultIfEmpty().Max(),
            samples.Any(item => item.Diagnostics.SessionTotalsAreLowerBounds));

        return new NetworkMetricSet(
            SumDouble(samples.Select(item => item.DownloadBytesPerSecond), processes.Length),
            SumDouble(samples.Select(item => item.UploadBytesPerSecond), processes.Length),
            SumUnsigned(samples.Select(item => item.SessionDownloadedBytes), processes.Length),
            SumUnsigned(samples.Select(item => item.SessionUploadedBytes), processes.Length),
            SumInt(samples.Select(item => item.ActiveTcpConnectionCount), processes.Length),
            SumInt(samples.Select(item => item.UdpEndpointCount), processes.Length),
            diagnostics);
    }

    private static MetricValue<double> SumDouble(IEnumerable<MetricValue<double>> values, int expectedCount)
    {
        MetricValue<double>[] items = values.ToArray();
        double[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length == 0)
        {
            return Unavailable<double>(items);
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
            return Unavailable<ulong>(items);
        }

        ulong sum = available.Aggregate(0UL, SaturatingAdd);
        return available.Length == expectedCount && items.All(item => item.IsComplete)
            ? MetricValue<ulong>.Available(sum)
            : MetricValue<ulong>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
    }

    private static MetricValue<int> SumInt(IEnumerable<MetricValue<int>> values, int expectedCount)
    {
        MetricValue<int>[] items = values.ToArray();
        int[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length == 0)
        {
            return Unavailable<int>(items);
        }

        int sum = available.Aggregate(0, SaturatingAdd);
        return available.Length == expectedCount && items.All(item => item.IsComplete)
            ? MetricValue<int>.Available(sum)
            : MetricValue<int>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
    }

    private static MetricValue<T> Unavailable<T>(IReadOnlyList<MetricValue<T>> values)
        where T : struct
    {
        foreach (MetricValue<T> item in values)
        {
            if (item.Availability != MetricAvailability.Available)
            {
                return MetricValue<T>.Unavailable(item.Availability, item.Detail);
            }
        }

        return MetricValue<T>.Unavailable(MetricAvailability.WarmingUp);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static int SaturatingAdd(int left, int right) =>
        int.MaxValue - left < right ? int.MaxValue : left + right;

    private static string PartialDetail(int complete, int total) =>
        $"Only {complete} of {total} network process samples were completely available; the displayed value is a lower bound.";
}
