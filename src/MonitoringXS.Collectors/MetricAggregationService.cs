using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class MetricAggregationService : IMetricAggregationService
{
    public IReadOnlyList<ApplicationMetricSnapshot> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<ProcessMetricSample> metrics,
        DateTimeOffset capturedAt)
    {
        Dictionary<ProcessInstanceId, ProcessMetricSample> metricsByProcess = metrics.ToDictionary(item => item.Process);

        return attribution
            .Where(item => !item.IsHidden && item.Application is not null)
            .GroupBy(item => item.Application!.LogicalApplicationId, StringComparer.Ordinal)
            .Select(group => CreateSnapshot(group, metricsByProcess, capturedAt))
            .OrderByDescending(item => item.CpuPercent.Value ?? -1d)
            .ThenBy(item => item.Application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static ApplicationMetricSnapshot CreateSnapshot(
        IGrouping<string, AttributionResult> group,
        IReadOnlyDictionary<ProcessInstanceId, ProcessMetricSample> metrics,
        DateTimeOffset capturedAt)
    {
        AttributionResult first = group.First();
        ProcessDescriptor[] processes = group.Select(item => item.Process).ToArray();
        ProcessMetricSample[] samples = processes
            .Select(process => metrics.GetValueOrDefault(process.InstanceId))
            .Where(sample => sample is not null)
            .Cast<ProcessMetricSample>()
            .ToArray();

        MetricValue<double> cpu = SumDouble(samples.Select(sample => sample.CpuPercent), processes.Length, clampToCpuCapacity: true);
        MetricValue<long> memory = SumLong(samples.Select(sample => sample.WorkingSetBytes), processes.Length);
        MetricValue<double> ioReadRate = SumDouble(samples.Select(sample => sample.IoReadBytesPerSecond), processes.Length);
        MetricValue<double> ioWriteRate = SumDouble(samples.Select(sample => sample.IoWriteBytesPerSecond), processes.Length);
        MetricValue<ulong> totalIoRead = SumUnsigned(samples.Select(sample => sample.TotalIoReadBytes), processes.Length);
        MetricValue<ulong> totalIoWrite = SumUnsigned(samples.Select(sample => sample.TotalIoWriteBytes), processes.Length);
        MetricValue<ulong> ioReadOperations = SumUnsigned(samples.Select(sample => sample.IoReadOperationCount), processes.Length);
        MetricValue<ulong> ioWriteOperations = SumUnsigned(samples.Select(sample => sample.IoWriteOperationCount), processes.Length);

        return new ApplicationMetricSnapshot(
            first.Application!,
            capturedAt,
            cpu,
            memory,
            ioReadRate,
            ioWriteRate,
            totalIoRead,
            totalIoWrite,
            ioReadOperations,
            ioWriteOperations,
            processes.Length,
            processes);
    }

    private static MetricValue<double> SumDouble(
        IEnumerable<MetricValue<double>> values,
        int expectedCount,
        bool clampToCpuCapacity = false)
    {
        MetricValue<double>[] items = values.ToArray();
        double[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length > 0)
        {
            double sum = available.Sum();
            if (clampToCpuCapacity)
            {
                sum = Math.Clamp(sum, 0d, 100d);
            }

            return available.Length == expectedCount && items.All(item => item.IsComplete)
                ? MetricValue<double>.Available(sum)
                : MetricValue<double>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
        }

        MetricAvailability availability = items.FirstOrDefault().Availability;
        return MetricValue<double>.Unavailable(availability == default ? MetricAvailability.WarmingUp : availability);
    }

    private static MetricValue<long> SumLong(IEnumerable<MetricValue<long>> values, int expectedCount)
    {
        MetricValue<long>[] items = values.ToArray();
        long[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length > 0)
        {
            long sum = available.Sum();
            return available.Length == expectedCount && items.All(item => item.IsComplete)
                ? MetricValue<long>.Available(sum)
                : MetricValue<long>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
        }

        MetricAvailability availability = items.FirstOrDefault().Availability;
        return MetricValue<long>.Unavailable(availability == default ? MetricAvailability.Error : availability);
    }

    private static MetricValue<ulong> SumUnsigned(IEnumerable<MetricValue<ulong>> values, int expectedCount)
    {
        MetricValue<ulong>[] items = values.ToArray();
        ulong[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length > 0)
        {
            ulong sum = available.Aggregate(0UL, SaturatingAdd);
            return available.Length == expectedCount && items.All(item => item.IsComplete)
                ? MetricValue<ulong>.Available(sum)
                : MetricValue<ulong>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
        }

        MetricAvailability availability = items.FirstOrDefault().Availability;
        return MetricValue<ulong>.Unavailable(availability == default ? MetricAvailability.Error : availability);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static string PartialDetail(int complete, int total) =>
        $"Only {complete} of {total} process samples were completely available; the displayed value is a lower bound.";
}
