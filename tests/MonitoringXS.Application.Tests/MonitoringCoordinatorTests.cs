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

    [Fact]
    public async Task CaptureAsyncEvictsHistoryForApplicationsThatAreNoLongerActive()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor process = new(new ProcessInstanceId(43, start), "tool", @"C:\Tools\tool.exe", "Tool", null, null, "Tool", null, false, true);
        ApplicationIdentity first = new("first", "First", null, ApplicationDisposition.Portable, @"C:\Tools", ClassificationConfidence.Medium, "test");
        ApplicationIdentity second = first with { LogicalApplicationId = "second", DisplayName = "Second" };
        MutableAttribution attribution = new(process, first);
        MutableAggregator aggregator = new(first, process);
        MonitoringCoordinator coordinator = new(
            new Discovery(process),
            attribution,
            new Collector(process),
            aggregator);

        MonitoringDashboardSnapshot firstCapture = await coordinator.CaptureAsync(CancellationToken.None);
        attribution.Identity = second;
        aggregator.Identity = second;
        MonitoringDashboardSnapshot secondCapture = await coordinator.CaptureAsync(CancellationToken.None);

        Assert.Contains("first", firstCapture.OneMinuteHistory.Keys);
        Assert.DoesNotContain("first", secondCapture.OneMinuteHistory.Keys);
        Assert.Contains("second", secondCapture.OneMinuteHistory.Keys);
    }

    [Fact]
    public async Task CaptureAsyncSamplesOnlyProcessesIncludedInApplicationTotals()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor visible = new(new ProcessInstanceId(44, start), "visible", @"C:\Apps\visible.exe", "Visible", null, null, "Visible", null, false, true);
        ProcessDescriptor hidden = new(new ProcessInstanceId(45, start), "hidden", null, null, null, null, null, null, true, false);
        ApplicationIdentity identity = new("visible", "Visible", null, ApplicationDisposition.Installed, @"C:\Apps", ClassificationConfidence.High, "test");
        RecordingCollector collector = new(visible);
        MonitoringCoordinator coordinator = new(
            new MultiDiscovery([visible, hidden]),
            new SelectiveAttribution(visible, hidden, identity),
            collector,
            new Aggregator(identity, visible));

        await coordinator.CaptureAsync(CancellationToken.None);

        ProcessDescriptor sampled = Assert.Single(collector.SampledProcesses);
        Assert.Equal(visible.InstanceId, sampled.InstanceId);
    }

    [Fact]
    public async Task CaptureAsyncMergesPhysicalDiskMetricsWithoutReplacingProcessIo()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor process = new(new ProcessInstanceId(46, start), "visible", @"C:\Apps\visible.exe", "Visible", null, null, "Visible", null, false, true);
        ApplicationIdentity identity = new("visible", "Visible", null, ApplicationDisposition.Installed, @"C:\Apps", ClassificationConfidence.High, "test");
        MonitoringCoordinator coordinator = new(
            new Discovery(process),
            new Attribution(process, identity),
            new Collector(process),
            new Aggregator(identity, process),
            new PhysicalCollector(process),
            new PhysicalAggregator(identity));

        MonitoringDashboardSnapshot dashboard = await coordinator.CaptureAsync(CancellationToken.None);

        ApplicationMetricSnapshot snapshot = Assert.Single(dashboard.InstalledApplications);
        Assert.Equal(10d, snapshot.IoReadBytesPerSecond.Value);
        Assert.Equal(123d, snapshot.PhysicalDisk.ReadBytesPerSecond.Value);
    }

    [Fact]
    public async Task CaptureAsyncMergesNetworkWithoutReplacingProcessIoOrPhysicalDisk()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor process = new(new ProcessInstanceId(47, start), "visible", @"C:\Apps\visible.exe", "Visible", null, null, "Visible", null, false, true);
        ApplicationIdentity identity = new("visible", "Visible", null, ApplicationDisposition.Installed, @"C:\Apps", ClassificationConfidence.High, "test");
        MonitoringCoordinator coordinator = new(
            new Discovery(process),
            new Attribution(process, identity),
            new Collector(process),
            new Aggregator(identity, process),
            new PhysicalCollector(process),
            new PhysicalAggregator(identity),
            new NetworkCollector(process),
            new NetworkAggregator(identity));

        MonitoringDashboardSnapshot dashboard = await coordinator.CaptureAsync(CancellationToken.None);

        ApplicationMetricSnapshot snapshot = Assert.Single(dashboard.InstalledApplications);
        Assert.Equal(10d, snapshot.IoReadBytesPerSecond.Value);
        Assert.Equal(123d, snapshot.PhysicalDisk.ReadBytesPerSecond.Value);
        Assert.Equal(789d, snapshot.Network.DownloadBytesPerSecond.Value);
    }

    [Fact]
    public async Task CaptureAsyncMergesGpuWithoutReplacingExistingMetrics()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor process = new(new ProcessInstanceId(48, start), "visible", @"C:\Apps\visible.exe", "Visible", null, null, "Visible", null, false, true);
        ApplicationIdentity identity = new("visible", "Visible", null, ApplicationDisposition.Installed, @"C:\Apps", ClassificationConfidence.High, "test");
        MonitoringCoordinator coordinator = new(
            new Discovery(process),
            new Attribution(process, identity),
            new Collector(process),
            new Aggregator(identity, process),
            new PhysicalCollector(process),
            new PhysicalAggregator(identity),
            new NetworkCollector(process),
            new NetworkAggregator(identity),
            new GpuCollector(process),
            new GpuAggregator(identity));

        MonitoringDashboardSnapshot dashboard = await coordinator.CaptureAsync(CancellationToken.None);

        ApplicationMetricSnapshot snapshot = Assert.Single(dashboard.InstalledApplications);
        Assert.Equal(10d, snapshot.IoReadBytesPerSecond.Value);
        Assert.Equal(123d, snapshot.PhysicalDisk.ReadBytesPerSecond.Value);
        Assert.Equal(789d, snapshot.Network.DownloadBytesPerSecond.Value);
        Assert.Equal(45d, snapshot.Gpu.UtilizationPercent.Value);
        Assert.Equal(256UL, snapshot.Gpu.DedicatedMemoryBytes.Value);
    }

    [Fact]
    public async Task GpuFailureDoesNotBreakOtherApplicationMetrics()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor process = new(new ProcessInstanceId(49, start), "visible", @"C:\Apps\visible.exe", "Visible", null, null, "Visible", null, false, true);
        ApplicationIdentity identity = new("visible", "Visible", null, ApplicationDisposition.Installed, @"C:\Apps", ClassificationConfidence.High, "test");
        MonitoringCoordinator coordinator = new(
            new Discovery(process),
            new Attribution(process, identity),
            new Collector(process),
            new Aggregator(identity, process),
            gpuCollector: new ThrowingGpuCollector(),
            gpuAggregation: new GpuAggregator(identity));

        MonitoringDashboardSnapshot dashboard = await coordinator.CaptureAsync(CancellationToken.None);

        ApplicationMetricSnapshot snapshot = Assert.Single(dashboard.InstalledApplications);
        Assert.Equal(10d, snapshot.IoReadBytesPerSecond.Value);
        Assert.Equal(MetricAvailability.Error, snapshot.Gpu.UtilizationPercent.Availability);
        Assert.Equal(GpuAvailabilityReason.CounterReadFailure, snapshot.Gpu.Reason);
    }

    [Fact]
    public async Task BrokerUnavailableDoesNotBreakCpuMemoryProcessIoOrGpu()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor process = new(
            new ProcessInstanceId(50, start),
            "visible",
            @"C:\Apps\visible.exe",
            "Visible",
            null,
            null,
            "Visible",
            null,
            false,
            true);
        ApplicationIdentity identity = new(
            "visible",
            "Visible",
            null,
            ApplicationDisposition.Installed,
            @"C:\Apps",
            ClassificationConfidence.High,
            "test");
        MonitoringCoordinator coordinator = new(
            new Discovery(process),
            new Attribution(process, identity),
            new Collector(process),
            new Aggregator(identity, process),
            new UnavailablePhysicalCollector(process),
            new PhysicalAggregator(identity),
            new UnavailableNetworkCollector(process),
            new NetworkAggregator(identity),
            new GpuCollector(process),
            new GpuAggregator(identity));

        MonitoringDashboardSnapshot dashboard =
            await coordinator.CaptureAsync(CancellationToken.None);

        ApplicationMetricSnapshot snapshot = Assert.Single(dashboard.InstalledApplications);
        Assert.Equal(1d, snapshot.CpuPercent.Value);
        Assert.Equal(1024, snapshot.WorkingSetBytes.Value);
        Assert.Equal(10d, snapshot.IoReadBytesPerSecond.Value);
        Assert.Equal(45d, snapshot.Gpu.UtilizationPercent.Value);
        Assert.Equal(
            MetricAvailability.Unavailable,
            snapshot.PhysicalDisk.ReadBytesPerSecond.Availability);
        Assert.Equal(
            MetricAvailability.Unavailable,
            snapshot.Network.DownloadBytesPerSecond.Availability);
    }

    private sealed class Discovery(ProcessDescriptor process) : IProcessDiscoveryService
    {
        public ValueTask<IReadOnlyList<ProcessDescriptor>> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<ProcessDescriptor>>([process]);
    }

    private sealed class MultiDiscovery(IReadOnlyList<ProcessDescriptor> processes) : IProcessDiscoveryService
    {
        public ValueTask<IReadOnlyList<ProcessDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(processes);
    }

    private sealed class Attribution(ProcessDescriptor process, ApplicationIdentity identity) : IApplicationAttributionService
    {
        public ValueTask<IReadOnlyList<AttributionResult>> AttributeAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AttributionResult>>([AttributionResult.Attributed(process, identity)]);
    }

    private sealed class MutableAttribution(ProcessDescriptor process, ApplicationIdentity identity) : IApplicationAttributionService
    {
        public ApplicationIdentity Identity { get; set; } = identity;

        public ValueTask<IReadOnlyList<AttributionResult>> AttributeAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AttributionResult>>([AttributionResult.Attributed(process, Identity)]);
    }

    private sealed class SelectiveAttribution(
        ProcessDescriptor visible,
        ProcessDescriptor hidden,
        ApplicationIdentity identity) : IApplicationAttributionService
    {
        public ValueTask<IReadOnlyList<AttributionResult>> AttributeAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AttributionResult>>(
            [
                AttributionResult.Attributed(visible, identity),
                AttributionResult.Hidden(hidden, "service")
            ]);
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

    private sealed class RecordingCollector(ProcessDescriptor process) : IProcessMetricCollector
    {
        public IReadOnlyList<ProcessDescriptor> SampledProcesses { get; private set; } = [];

        public ValueTask<IReadOnlyList<ProcessMetricSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken)
        {
            SampledProcesses = processes.ToArray();
            return new Collector(process).CollectAsync(processes, capturedAt, cancellationToken);
        }
    }

    private sealed class PhysicalCollector(ProcessDescriptor process) : IPhysicalDiskMetricCollector
    {
        public ValueTask<IReadOnlyList<PhysicalDiskProcessSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<PhysicalDiskProcessSample>>([new(
                process.InstanceId,
                capturedAt.ToUniversalTime(),
                MetricValue<double>.Available(123),
                MetricValue<double>.Available(456),
                MetricValue<ulong>.Available(123),
                MetricValue<ulong>.Available(456),
                MetricValue<ulong>.Available(1),
                MetricValue<ulong>.Available(2),
                default)]);
    }

    private sealed class UnavailablePhysicalCollector(ProcessDescriptor process)
        : IPhysicalDiskMetricCollector
    {
        public ValueTask<IReadOnlyList<PhysicalDiskProcessSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PhysicalDiskProcessSample>>(
            [
                new(
                    process.InstanceId,
                    capturedAt.ToUniversalTime(),
                    MetricValue<double>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<double>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                    default)
            ]);
    }

    private sealed class PhysicalAggregator(ApplicationIdentity identity) : IPhysicalDiskAggregationService
    {
        public IReadOnlyDictionary<string, PhysicalDiskMetricSet> Aggregate(
            IReadOnlyList<AttributionResult> attribution,
            IReadOnlyList<PhysicalDiskProcessSample> metrics) => new Dictionary<string, PhysicalDiskMetricSet>(StringComparer.Ordinal)
            {
                [identity.LogicalApplicationId] = new(
                    metrics[0].ReadBytesPerSecond,
                    metrics[0].WriteBytesPerSecond,
                    metrics[0].SessionReadBytes,
                    metrics[0].SessionWriteBytes,
                    metrics[0].SessionReadOperationCount,
                    metrics[0].SessionWriteOperationCount,
                    metrics[0].Diagnostics)
            };
    }

    private sealed class NetworkCollector(ProcessDescriptor process) : INetworkMetricCollector
    {
        public ValueTask<IReadOnlyList<NetworkProcessSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<NetworkProcessSample>>([new(
                process.InstanceId,
                capturedAt.ToUniversalTime(),
                MetricValue<double>.Available(789),
                MetricValue<double>.Available(987),
                MetricValue<ulong>.Available(789),
                MetricValue<ulong>.Available(987),
                MetricValue<int>.Unavailable(MetricAvailability.Unsupported),
                MetricValue<int>.Unavailable(MetricAvailability.Unsupported),
                default)]);
    }

    private sealed class UnavailableNetworkCollector(ProcessDescriptor process)
        : INetworkMetricCollector
    {
        public ValueTask<IReadOnlyList<NetworkProcessSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<NetworkProcessSample>>(
            [
                new(
                    process.InstanceId,
                    capturedAt.ToUniversalTime(),
                    MetricValue<double>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<double>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<int>.Unavailable(MetricAvailability.Unavailable),
                    MetricValue<int>.Unavailable(MetricAvailability.Unavailable),
                    default)
            ]);
    }

    private sealed class NetworkAggregator(ApplicationIdentity identity) : INetworkMetricAggregationService
    {
        public IReadOnlyDictionary<string, NetworkMetricSet> Aggregate(
            IReadOnlyList<AttributionResult> attribution,
            IReadOnlyList<NetworkProcessSample> metrics) => new Dictionary<string, NetworkMetricSet>(StringComparer.Ordinal)
            {
                [identity.LogicalApplicationId] = new(
                    metrics[0].DownloadBytesPerSecond,
                    metrics[0].UploadBytesPerSecond,
                    metrics[0].SessionDownloadedBytes,
                    metrics[0].SessionUploadedBytes,
                    metrics[0].ActiveTcpConnectionCount,
                    metrics[0].UdpEndpointCount,
                    metrics[0].Diagnostics)
            };
    }

    private sealed class GpuCollector(ProcessDescriptor process) : IGpuMetricCollector
    {
        public ValueTask<IReadOnlyList<GpuProcessSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<GpuProcessSample>>(
            [
                new(
                    process.InstanceId,
                    capturedAtUtc,
                    MetricValue<double>.Available(45),
                    MetricValue<ulong>.Available(256),
                    MetricValue<ulong>.Available(128),
                    [new(new GpuEngineId(1, 0, 0, "3D"), 45)],
                    new GpuCollectorDiagnostics
                    {
                        ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                        CollectorStatus = MetricAvailability.Available,
                        Reason = GpuAvailabilityReason.None
                    })
            ]);
    }

    private sealed class ThrowingGpuCollector : IGpuMetricCollector
    {
        public ValueTask<IReadOnlyList<GpuProcessSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated PDH failure.");
    }

    private sealed class GpuAggregator(ApplicationIdentity identity) : IGpuMetricAggregationService
    {
        public IReadOnlyDictionary<string, GpuMetricSet> Aggregate(
            IReadOnlyList<AttributionResult> attribution,
            IReadOnlyList<GpuProcessSample> metrics) =>
            new Dictionary<string, GpuMetricSet>(StringComparer.Ordinal)
            {
                [identity.LogicalApplicationId] = new(
                    metrics[0].UtilizationPercent,
                    metrics[0].DedicatedMemoryBytes,
                    metrics[0].SharedMemoryBytes,
                    metrics[0].Engines[0].Engine,
                    metrics[0].Diagnostics)
            };
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

    private sealed class MutableAggregator(ApplicationIdentity identity, ProcessDescriptor process) : IMetricAggregationService
    {
        public ApplicationIdentity Identity { get; set; } = identity;

        public IReadOnlyList<ApplicationMetricSnapshot> Aggregate(
            IReadOnlyList<AttributionResult> attribution,
            IReadOnlyList<ProcessMetricSample> metrics,
            DateTimeOffset capturedAt) =>
            [new(
                Identity,
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
