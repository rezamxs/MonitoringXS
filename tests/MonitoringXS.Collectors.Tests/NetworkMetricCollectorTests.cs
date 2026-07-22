using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class NetworkMetricCollectorTests
{
    [Fact]
    public async Task DownloadBytesAreAttributedToTheProcess()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(100, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(),
            Available(Event(process, now.AddMilliseconds(10), NetworkDirection.Download, 2048)));

        await collector.CollectAsync([process], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None));

        Assert.Equal(2048d, sample.DownloadBytesPerSecond.Value);
        Assert.Equal(2048UL, sample.SessionDownloadedBytes.Value);
    }

    [Fact]
    public async Task UploadBytesAreAttributedToTheProcess()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(101, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(),
            Available(Event(process, now.AddMilliseconds(10), NetworkDirection.Upload, 4096)));

        await collector.CollectAsync([process], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None));

        Assert.Equal(4096d, sample.UploadBytesPerSecond.Value);
        Assert.Equal(4096UL, sample.SessionUploadedBytes.Value);
    }

    [Fact]
    public async Task RateUsesTheActualUtcCaptureInterval()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(102, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(),
            Available(Event(process, now.AddSeconds(1), NetworkDirection.Download, 4096)));

        await collector.CollectAsync([process], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(2048d, sample.DownloadBytesPerSecond.Value);
    }

    [Fact]
    public async Task SessionTotalsAccumulateAcrossCaptures()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(103, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(Event(process, now, NetworkDirection.Download, 100)),
            Available(Event(process, now.AddSeconds(1), NetworkDirection.Download, 200)),
            Available(Event(process, now.AddSeconds(2), NetworkDirection.Upload, 300)));

        await collector.CollectAsync([process], now, CancellationToken.None);
        await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(300UL, sample.SessionDownloadedBytes.Value);
        Assert.Equal(300UL, sample.SessionUploadedBytes.Value);
    }

    [Fact]
    public void MultipleProcessesAggregateIntoOneLogicalApplication()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(104, now);
        ProcessDescriptor second = Process(105, now);
        ApplicationIdentity app = App("app", "App");

        NetworkMetricSet result = new NetworkMetricAggregationService().Aggregate(
            [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)],
            [Sample(first, now, 10, 20, 100, 200), Sample(second, now, 30, 40, 300, 400)])[app.LogicalApplicationId];

        Assert.Equal(40d, result.DownloadBytesPerSecond.Value);
        Assert.Equal(60d, result.UploadBytesPerSecond.Value);
        Assert.Equal(400UL, result.SessionDownloadedBytes.Value);
        Assert.Equal(600UL, result.SessionUploadedBytes.Value);
    }

    [Fact]
    public void UnrelatedLogicalApplicationsRemainSeparate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(106, now);
        ProcessDescriptor second = Process(107, now);
        ApplicationIdentity firstApp = App("first", "First");
        ApplicationIdentity secondApp = App("second", "Second");

        IReadOnlyDictionary<string, NetworkMetricSet> result = new NetworkMetricAggregationService().Aggregate(
            [AttributionResult.Attributed(first, firstApp), AttributionResult.Attributed(second, secondApp)],
            [Sample(first, now, 10, 0, 10, 0), Sample(second, now, 20, 0, 20, 0)]);

        Assert.Equal(10d, result["first"].DownloadBytesPerSecond.Value);
        Assert.Equal(20d, result["second"].DownloadBytesPerSecond.Value);
    }

    [Fact]
    public async Task ReusedPidStartsWithNewBoundedSessionState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor oldProcess = Process(108, now.AddMinutes(-2));
        ProcessDescriptor newProcess = Process(108, now.AddSeconds(1));
        NetworkMetricCollector collector = Collector(
            Available(Event(oldProcess, now, NetworkDirection.Download, 500)),
            Available(Event(newProcess, now.AddSeconds(2), NetworkDirection.Download, 50)));

        await collector.CollectAsync([oldProcess], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([newProcess], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(50UL, sample.SessionDownloadedBytes.Value);
        Assert.Equal(MetricAvailability.WarmingUp, sample.DownloadBytesPerSecond.Availability);
    }

    [Fact]
    public async Task OldEventIsRejectedAfterPidReuse()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(109, start);
        NetworkMetricCollector collector = Collector(
            Available(Event(process, start.AddMilliseconds(-1), NetworkDirection.Download, 8192)),
            Available());

        NetworkProcessSample first = Assert.Single(await collector.CollectAsync([process], start, CancellationToken.None));
        NetworkProcessSample second = Assert.Single(await collector.CollectAsync([process], start.AddSeconds(1), CancellationToken.None));

        Assert.Equal(0UL, first.SessionDownloadedBytes.Value);
        Assert.Equal(1, second.Diagnostics.PidReuseEventsRejected);
    }

    [Fact]
    public async Task ProcessExitEvictsCollectorState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(110, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(Event(process, now, NetworkDirection.Download, 500)),
            Available(),
            Available());

        await collector.CollectAsync([process], now, CancellationToken.None);
        Assert.Empty(await collector.CollectAsync([], now.AddSeconds(1), CancellationToken.None));
        NetworkProcessSample restarted = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(0UL, restarted.SessionDownloadedBytes.Value);
    }

    [Fact]
    public async Task FirstHealthyCaptureIsWarmingUp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(111, now.AddMinutes(-1));
        NetworkProcessSample sample = Assert.Single(await Collector(Available()).CollectAsync(
            [process], now, CancellationToken.None));

        Assert.Equal(MetricAvailability.WarmingUp, sample.DownloadBytesPerSecond.Availability);
        Assert.Null(sample.DownloadBytesPerSecond.Value);
    }

    [Fact]
    public async Task HealthyZeroTrafficIntervalReportsRealZero()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(112, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(Available(), Available());

        await collector.CollectAsync([process], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None));

        Assert.Equal(MetricAvailability.Available, sample.DownloadBytesPerSecond.Availability);
        Assert.Equal(0d, sample.DownloadBytesPerSecond.Value);
    }

    [Fact]
    public async Task UnavailableIsNotConvertedToZero()
    {
        ProcessDescriptor process = Process(113, DateTimeOffset.UtcNow.AddMinutes(-1));
        NetworkProcessSample sample = Assert.Single(await Collector(Unavailable(
            MetricAvailability.Unavailable,
            NetworkAvailabilityReason.CollectorError)).CollectAsync(
            [process], DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(MetricAvailability.Unavailable, sample.DownloadBytesPerSecond.Availability);
        Assert.Null(sample.DownloadBytesPerSecond.Value);
        Assert.Null(sample.SessionDownloadedBytes.Value);
    }

    [Fact]
    public async Task PartialValuesUseLowerBoundSemantics()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(114, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(),
            Available(Event(process, now, NetworkDirection.Download, 512)) with
            {
                Availability = MetricAvailability.Partial,
                Reason = NetworkAvailabilityReason.EventLoss
            });

        await collector.CollectAsync([process], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None));

        Assert.Equal(MetricAvailability.Partial, sample.DownloadBytesPerSecond.Availability);
        Assert.Contains("lower bounds", sample.DownloadBytesPerSecond.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EtwEventLossKeepsSessionTotalsPartialAfterRecovery()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(115, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(),
            Available(Event(process, now, NetworkDirection.Download, 100)) with
            {
                EtwEventsLost = 1,
                Reason = NetworkAvailabilityReason.EventLoss
            },
            Available());

        await collector.CollectAsync([process], now, CancellationToken.None);
        await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None);
        NetworkProcessSample recovered = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(MetricAvailability.Available, recovered.DownloadBytesPerSecond.Availability);
        Assert.Equal(MetricAvailability.Partial, recovered.SessionDownloadedBytes.Availability);
        Assert.True(recovered.Diagnostics.SessionTotalsAreLowerBounds);
    }

    [Fact]
    public async Task QueueOverflowMarksCurrentAndSessionValuesPartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(116, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available(),
            Available() with
            {
                QueueEventsDropped = 3,
                Reason = NetworkAvailabilityReason.ResourceExhausted
            });

        await collector.CollectAsync([process], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None));

        Assert.Equal(MetricAvailability.Partial, sample.DownloadBytesPerSecond.Availability);
        Assert.Equal(MetricAvailability.Partial, sample.SessionDownloadedBytes.Availability);
        Assert.Equal(3, sample.Diagnostics.QueueEventsDropped);
    }

    [Fact]
    public async Task AccessDeniedIsExplicitAndHasNoValue()
    {
        ProcessDescriptor process = Process(117, DateTimeOffset.UtcNow.AddMinutes(-1));
        NetworkProcessSample sample = Assert.Single(await Collector(Unavailable(
            MetricAvailability.AccessDenied,
            NetworkAvailabilityReason.AccessDenied)).CollectAsync(
            [process], DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(MetricAvailability.AccessDenied, sample.DownloadBytesPerSecond.Availability);
        Assert.Equal(NetworkAvailabilityReason.AccessDenied, sample.Diagnostics.Reason);
        Assert.Null(sample.DownloadBytesPerSecond.Value);
    }

    [Fact]
    public async Task UnsupportedPlatformIsExplicit()
    {
        ProcessDescriptor process = Process(118, DateTimeOffset.UtcNow.AddMinutes(-1));
        NetworkProcessSample sample = Assert.Single(await Collector(Unavailable(
            MetricAvailability.Unsupported,
            NetworkAvailabilityReason.Unsupported)).CollectAsync(
            [process], DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(MetricAvailability.Unsupported, sample.DownloadBytesPerSecond.Availability);
        Assert.Equal(NetworkAvailabilityReason.Unsupported, sample.Diagnostics.Reason);
    }

    [Fact]
    public async Task SessionConflictIsExplicit()
    {
        ProcessDescriptor process = Process(119, DateTimeOffset.UtcNow.AddMinutes(-1));
        NetworkProcessSample sample = Assert.Single(await Collector(Unavailable(
            MetricAvailability.Unavailable,
            NetworkAvailabilityReason.SessionConflict)).CollectAsync(
            [process], DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(MetricAvailability.Unavailable, sample.DownloadBytesPerSecond.Availability);
        Assert.Equal(NetworkAvailabilityReason.SessionConflict, sample.Diagnostics.Reason);
    }

    [Fact]
    public async Task CancellationIsPropagatedForCleanShutdown()
    {
        ProcessDescriptor process = Process(120, DateTimeOffset.UtcNow.AddMinutes(-1));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        NetworkMetricCollector collector = new(new CancellingSource());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await collector.CollectAsync([process], DateTimeOffset.UtcNow, cancellation.Token));
    }

    [Fact]
    public async Task DiagnosticsPreserveBoundedQueueMeasurements()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(121, now.AddMinutes(-1));
        NetworkMetricCollector collector = Collector(
            Available() with { EventsObserved = 10, MaximumQueueDepth = 8, EtwBufferSizeMegabytes = 32 },
            Available() with { EventsObserved = 30, CurrentQueueDepth = 4, MaximumQueueDepth = 16, EtwBufferSizeMegabytes = 32 });

        await collector.CollectAsync([process], now, CancellationToken.None);
        NetworkProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(10d, sample.Diagnostics.EventRatePerSecond);
        Assert.Equal(4, sample.Diagnostics.CurrentQueueDepth);
        Assert.Equal(16, sample.Diagnostics.MaximumQueueDepth);
        Assert.Equal(32, sample.Diagnostics.EtwBufferSizeMegabytes);
    }

    [Fact]
    public async Task ReliableEndpointSnapshotsAreExposedWithoutGuessing()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(122, now.AddMinutes(-1));
        NetworkEventBatch batch = Available() with
        {
            ActiveTcpConnectionsByProcess = new Dictionary<int, int> { [122] = 3 },
            UdpEndpointsByProcess = new Dictionary<int, int> { [122] = 2 }
        };

        NetworkProcessSample sample = Assert.Single(await Collector(batch).CollectAsync(
            [process], now, CancellationToken.None));

        Assert.Equal(3, sample.ActiveTcpConnectionCount.Value);
        Assert.Equal(2, sample.UdpEndpointCount.Value);
    }

    private static NetworkMetricCollector Collector(params NetworkEventBatch[] batches) =>
        new(new SequenceSource(batches));

    private static NetworkEventBatch Available(params NetworkTrafficEvent[] events) =>
        new(events, MetricAvailability.Available, NetworkAvailabilityReason.None, 0, 0, 0);

    private static NetworkEventBatch Unavailable(
        MetricAvailability availability,
        NetworkAvailabilityReason reason) => new([], availability, reason, 0, 0, 0, reason.ToString());

    private static NetworkTrafficEvent Event(
        ProcessDescriptor process,
        DateTimeOffset timestamp,
        NetworkDirection direction,
        int bytes) => new(process.InstanceId.ProcessId, timestamp, direction, NetworkTransport.Tcp, bytes);

    private static NetworkProcessSample Sample(
        ProcessDescriptor process,
        DateTimeOffset capturedAt,
        double downloadRate,
        double uploadRate,
        ulong downloaded,
        ulong uploaded) => new(
        process.InstanceId,
        capturedAt,
        MetricValue<double>.Available(downloadRate),
        MetricValue<double>.Available(uploadRate),
        MetricValue<ulong>.Available(downloaded),
        MetricValue<ulong>.Available(uploaded),
        MetricValue<int>.Unavailable(MetricAvailability.Unsupported),
        MetricValue<int>.Unavailable(MetricAvailability.Unsupported),
        default);

    private static ProcessDescriptor Process(int pid, DateTimeOffset start) =>
        new(new ProcessInstanceId(pid, start), "test", null, null, null, null, null, null, false, true);

    private static ApplicationIdentity App(string id, string name) =>
        new(id, name, null, ApplicationDisposition.Installed, null, ClassificationConfidence.High, "test");

    private sealed class SequenceSource(params NetworkEventBatch[] batches) : INetworkEventSource
    {
        private readonly Queue<NetworkEventBatch> _batches = new(batches);

        public ValueTask<NetworkEventBatch> ReadNetworkBatchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_batches.Dequeue());
        }
    }

    private sealed class CancellingSource : INetworkEventSource
    {
        public ValueTask<NetworkEventBatch> ReadNetworkBatchAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<NetworkEventBatch>(cancellationToken);
    }
}
