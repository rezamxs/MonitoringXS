using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application.Tests;

public sealed class MonitoringCoordinatorTests
{
    [Fact]
    public async Task CaptureAsyncSeparatesPortableApplications()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor process = new(new ProcessInstanceId(42, start), "tool", @"C:\Tools\tool.exe", "Tool", null, null, "Tool", null, false, true);
        ApplicationIdentity identity = new("tool", "Tool", null, ApplicationDisposition.Portable, @"C:\Tools", ClassificationConfidence.Medium, "test");
        MonitoringCoordinator coordinator = new(
            new Discovery(process),
            new Attribution(process, identity),
            new Collector(process),
            new Aggregator(identity, process));

        MonitoringDashboardSnapshot result = await coordinator.CaptureAsync(CancellationToken.None);

        Assert.Empty(result.InstalledApplications);
        Assert.Single(result.PortableApplications);
        Assert.Single(result.OneMinuteHistory["tool"]);
    }

    private sealed class Discovery(ProcessDescriptor process) : IProcessDiscoveryService
    {
        public ValueTask<IReadOnlyList<ProcessDescriptor>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<ProcessDescriptor>>([process]);
    }

    private sealed class Attribution(ProcessDescriptor process, ApplicationIdentity identity) : IApplicationAttributionService
    {
        public IReadOnlyList<AttributionResult> Attribute(IReadOnlyList<ProcessDescriptor> processes) => [AttributionResult.Attributed(process, identity)];
    }

    private sealed class Collector(ProcessDescriptor process) : IProcessMetricCollector
    {
        public ValueTask<IReadOnlyList<ProcessMetricSample>> CollectAsync(IReadOnlyList<ProcessDescriptor> processes, DateTimeOffset capturedAt, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProcessMetricSample>>([new(
                process.InstanceId,
                capturedAt,
                MetricValue<double>.Available(1),
                MetricValue<long>.Available(1024),
                MetricValue<double>.Available(10),
                MetricValue<double>.Available(20),
                MetricValue<ulong>.Available(100),
                MetricValue<ulong>.Available(200),
                MetricValue<ulong>.Available(1),
                MetricValue<ulong>.Available(2))]);
    }

    private sealed class Aggregator(ApplicationIdentity identity, ProcessDescriptor process) : IMetricAggregationService
    {
        public IReadOnlyList<ApplicationMetricSnapshot> Aggregate(IReadOnlyList<AttributionResult> attribution, IReadOnlyList<ProcessMetricSample> metrics, DateTimeOffset capturedAt) =>
            [new(
                identity,
                capturedAt,
                metrics[0].CpuPercent,
                metrics[0].WorkingSetBytes,
                metrics[0].IoReadBytesPerSecond,
                metrics[0].IoWriteBytesPerSecond,
                metrics[0].TotalIoReadBytes,
                metrics[0].TotalIoWriteBytes,
                metrics[0].IoReadOperationCount,
                metrics[0].IoWriteOperationCount,
                1,
                [process])];
    }
}
