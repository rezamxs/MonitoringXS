using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class PhysicalDiskAggregationServiceTests
{
    [Fact]
    public void AggregateSumsPhysicalDiskProcessesWithinLogicalApplication()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(50, now);
        ProcessDescriptor second = Process(51, now);
        ApplicationIdentity app = App();

        PhysicalDiskMetricSet result = new PhysicalDiskAggregationService().Aggregate(
            [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)],
            [Sample(first, now, 10, 20, 100, 200), Sample(second, now, 30, 40, 300, 400)])[app.LogicalApplicationId];

        Assert.Equal(40d, result.ReadBytesPerSecond.Value);
        Assert.Equal(60d, result.WriteBytesPerSecond.Value);
        Assert.Equal(400UL, result.SessionReadBytes.Value);
        Assert.Equal(600UL, result.SessionWriteBytes.Value);
        Assert.Equal(2UL, result.SessionReadOperationCount.Value);
    }

    [Fact]
    public void MissingProcessSampleMakesApplicationValuePartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(52, now);
        ProcessDescriptor second = Process(53, now);
        ApplicationIdentity app = App();

        PhysicalDiskMetricSet result = new PhysicalDiskAggregationService().Aggregate(
            [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)],
            [Sample(first, now, 10, 20, 100, 200)])[app.LogicalApplicationId];

        Assert.Equal(MetricAvailability.Partial, result.ReadBytesPerSecond.Availability);
        Assert.Contains("1 of 2", result.ReadBytesPerSecond.Detail, StringComparison.Ordinal);
    }

    private static PhysicalDiskProcessSample Sample(
        ProcessDescriptor process,
        DateTimeOffset capturedAt,
        double readRate,
        double writeRate,
        ulong readBytes,
        ulong writeBytes) => new(
            process.InstanceId,
            capturedAt,
            MetricValue<double>.Available(readRate),
            MetricValue<double>.Available(writeRate),
            MetricValue<ulong>.Available(readBytes),
            MetricValue<ulong>.Available(writeBytes),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(2),
            default);

    private static ProcessDescriptor Process(int pid, DateTimeOffset start) =>
        new(new ProcessInstanceId(pid, start), "test", null, null, null, null, null, null, false, true);

    private static ApplicationIdentity App() =>
        new("app", "App", null, ApplicationDisposition.Installed, null, ClassificationConfidence.High, "test");
}
