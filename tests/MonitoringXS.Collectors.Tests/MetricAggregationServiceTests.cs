using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class MetricAggregationServiceTests
{
    [Fact]
    public void AggregateSumsProcessesWithinLogicalApplication()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(10, now, "chrome");
        ProcessDescriptor second = Process(11, now, "chrome");
        ApplicationIdentity app = new("google-chrome", "Google Chrome", "Google", ApplicationDisposition.Installed, null, ClassificationConfidence.High, "test");
        AttributionResult[] attribution = [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)];
        ProcessMetricSample[] metrics =
        [
            Sample(first.InstanceId, now, cpu: 4, memory: 100, readRate: 10, writeRate: 20, readBytes: 100, writeBytes: 200),
            Sample(second.InstanceId, now, cpu: 6, memory: 150, readRate: 30, writeRate: 40, readBytes: 300, writeBytes: 400)
        ];

        ApplicationMetricSnapshot result = Assert.Single(new MetricAggregationService().Aggregate(attribution, metrics, now));

        Assert.Equal(10, result.CpuPercent.Value);
        Assert.Equal(250, result.WorkingSetBytes.Value);
        Assert.Equal(40, result.IoReadBytesPerSecond.Value);
        Assert.Equal(60, result.IoWriteBytesPerSecond.Value);
        Assert.Equal(400UL, result.TotalIoReadBytes.Value);
        Assert.Equal(600UL, result.TotalIoWriteBytes.Value);
        Assert.Equal(2, result.ProcessCount);
    }

    [Fact]
    public void AggregateMarksIncompleteApplicationTotalsAsPartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(10, now, "app");
        ProcessDescriptor second = Process(11, now, "app");
        ApplicationIdentity app = new("app", "App", null, ApplicationDisposition.Installed, null, ClassificationConfidence.High, "test");
        ProcessMetricSample available = Sample(first.InstanceId, now, cpu: 2, memory: 100, readRate: 10, writeRate: 20, readBytes: 100, writeBytes: 200);
        ProcessMetricSample unavailable = new(
            second.InstanceId,
            now,
            MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<long>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied));

        ApplicationMetricSnapshot result = Assert.Single(new MetricAggregationService().Aggregate(
            [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)],
            [available, unavailable],
            now));

        Assert.Equal(MetricAvailability.Partial, result.CpuPercent.Availability);
        Assert.Equal(MetricAvailability.Partial, result.IoReadBytesPerSecond.Availability);
        Assert.Contains("lower bound", result.IoReadBytesPerSecond.Detail, StringComparison.OrdinalIgnoreCase);

        ApplicationMetricSnapshot missingSample = Assert.Single(new MetricAggregationService().Aggregate(
            [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)],
            [available],
            now));

        Assert.Equal(MetricAvailability.Partial, missingSample.CpuPercent.Availability);
        Assert.Contains("1 of 2", missingSample.CpuPercent.Detail, StringComparison.Ordinal);
    }

    private static ProcessMetricSample Sample(
        ProcessInstanceId process,
        DateTimeOffset capturedAt,
        double cpu,
        long memory,
        double readRate,
        double writeRate,
        ulong readBytes,
        ulong writeBytes) => new(
            process,
            capturedAt,
            MetricValue<double>.Available(cpu),
            MetricValue<long>.Available(memory),
            MetricValue<double>.Available(readRate),
            MetricValue<double>.Available(writeRate),
            MetricValue<ulong>.Available(readBytes),
            MetricValue<ulong>.Available(writeBytes),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(2));

    private static ProcessDescriptor Process(int pid, DateTimeOffset start, string name) =>
        new(new ProcessInstanceId(pid, start), name, null, null, null, null, null, null, false, true);
}
