using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class PhysicalDiskMetricCollectorTests
{
    [Fact]
    public async Task FirstCaptureWarmsUpAndHealthyZeroBecomesAvailable()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(42, now.AddMinutes(-1));
        PhysicalDiskMetricCollector collector = new(new SequenceSource(Available(), Available()));

        PhysicalDiskProcessSample first = Assert.Single(await collector.CollectAsync([process], now, CancellationToken.None));
        PhysicalDiskProcessSample second = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(MetricAvailability.WarmingUp, first.ReadBytesPerSecond.Availability);
        Assert.Equal(0d, second.ReadBytesPerSecond.Value);
        Assert.Equal(MetricAvailability.Available, second.ReadBytesPerSecond.Availability);
        Assert.Equal(0UL, second.SessionReadBytes.Value);
    }

    [Fact]
    public async Task CaptureCalculatesReadWriteRatesOperationsAndSessionTotals()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(43, now.AddMinutes(-1));
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            Available(
                Event(process, now.AddMilliseconds(100), PhysicalDiskOperation.Read, 2048),
                Event(process, now.AddMilliseconds(200), PhysicalDiskOperation.Write, 4096))));

        await collector.CollectAsync([process], now, CancellationToken.None);
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(2), CancellationToken.None));

        Assert.Equal(1024d, sample.ReadBytesPerSecond.Value);
        Assert.Equal(2048d, sample.WriteBytesPerSecond.Value);
        Assert.Equal(2048UL, sample.SessionReadBytes.Value);
        Assert.Equal(4096UL, sample.SessionWriteBytes.Value);
        Assert.Equal(1UL, sample.SessionReadOperationCount.Value);
        Assert.Equal(1UL, sample.SessionWriteOperationCount.Value);
    }

    [Fact]
    public async Task EventBeforeUtcProcessStartIsRejectedAsPidReuse()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(44, start);
        PhysicalDiskIoEvent stale = Event(process, start.AddMilliseconds(-1), PhysicalDiskOperation.Read, 8192);
        PhysicalDiskMetricCollector collector = new(new SequenceSource(Available(stale), Available()));

        PhysicalDiskProcessSample first = Assert.Single(await collector.CollectAsync([process], start.AddSeconds(1), CancellationToken.None));
        PhysicalDiskProcessSample second = Assert.Single(await collector.CollectAsync([process], start.AddSeconds(2), CancellationToken.None));

        Assert.Equal(0UL, first.SessionReadBytes.Value);
        Assert.Equal(1, second.Diagnostics.PidReuseEventsRejected);
        Assert.Equal(MetricAvailability.Available, second.ReadBytesPerSecond.Availability);
    }

    [Fact]
    public async Task EventAndProcessOffsetsCompareInNormalizedUtcDomain()
    {
        DateTimeOffset startWithOffset = new(2026, 7, 21, 12, 0, 0, TimeSpan.FromHours(3.5));
        ProcessDescriptor process = Process(45, startWithOffset);
        DateTimeOffset eventWithUtcOffset = startWithOffset.ToUniversalTime().AddMilliseconds(1);
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            Available(new PhysicalDiskIoEvent(45, 10, eventWithUtcOffset, PhysicalDiskOperation.Read, 512))));

        await collector.CollectAsync([process], startWithOffset.AddSeconds(1), CancellationToken.None);
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            startWithOffset.AddSeconds(2),
            CancellationToken.None));

        Assert.Equal(512UL, sample.SessionReadBytes.Value);
        Assert.Equal(0, sample.Diagnostics.PidReuseEventsRejected);
    }

    [Fact]
    public async Task LostEtwEventsMarkValuesAsPartialLowerBounds()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(46, now.AddMinutes(-1));
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            new PhysicalDiskEventBatch(
                [Event(process, now.AddMilliseconds(1), PhysicalDiskOperation.Read, 1024)],
                MetricAvailability.Available,
                2,
                0,
                0)));

        await collector.CollectAsync([process], now, CancellationToken.None);
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync([process], now.AddSeconds(1), CancellationToken.None));

        Assert.Equal(MetricAvailability.Partial, sample.ReadBytesPerSecond.Availability);
        Assert.Equal(1024d, sample.ReadBytesPerSecond.Value);
        Assert.Contains("lower bounds", sample.ReadBytesPerSecond.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccessDeniedIsPropagatedWithoutFabricatedValues()
    {
        ProcessDescriptor process = Process(47, DateTimeOffset.UtcNow.AddMinutes(-1));
        PhysicalDiskEventBatch denied = new([], MetricAvailability.AccessDenied, 0, 0, 0, "denied");
        PhysicalDiskMetricCollector collector = new(new SequenceSource(denied));

        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        Assert.Equal(MetricAvailability.AccessDenied, sample.ReadBytesPerSecond.Availability);
        Assert.Null(sample.ReadBytesPerSecond.Value);
        Assert.Null(sample.SessionReadBytes.Value);
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        ProcessDescriptor process = Process(48, DateTimeOffset.UtcNow.AddMinutes(-1));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        PhysicalDiskMetricCollector collector = new(new CancellingSource());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await collector.CollectAsync([process], DateTimeOffset.UtcNow, cancellation.Token));
    }

    [Fact]
    public async Task DiagnosticsCalculateObservedEventRateAndPreserveQueueMeasurements()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(49, now.AddMinutes(-1));
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 0, EventsObserved: 10, MaximumQueueDepth: 3, EtwBufferSizeMegabytes: 32),
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 0, EventsObserved: 30, CurrentQueueDepth: 2, MaximumQueueDepth: 7, EtwBufferSizeMegabytes: 32)));

        await collector.CollectAsync([process], now, CancellationToken.None);
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(2),
            CancellationToken.None));

        Assert.Equal(10d, sample.Diagnostics.EventRatePerSecond);
        Assert.Equal(30, sample.Diagnostics.EventsObserved);
        Assert.Equal(2, sample.Diagnostics.CurrentQueueDepth);
        Assert.Equal(7, sample.Diagnostics.MaximumQueueDepth);
        Assert.Equal(32, sample.Diagnostics.EtwBufferSizeMegabytes);
    }

    [Fact]
    public async Task UnattributedSystemEventsRemainVisibleWithoutDegradingMappedApplication()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(50, now.AddMinutes(-1));
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 10),
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 20)));

        await collector.CollectAsync([process], now, CancellationToken.None);
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(1),
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Available, sample.ReadBytesPerSecond.Availability);
        Assert.Equal(20, sample.Diagnostics.UnattributedEvents);
    }

    private static PhysicalDiskEventBatch Available(params PhysicalDiskIoEvent[] events) =>
        new(events, MetricAvailability.Available, 0, 0, 0);

    private static PhysicalDiskIoEvent Event(
        ProcessDescriptor process,
        DateTimeOffset timestamp,
        PhysicalDiskOperation operation,
        int bytes) => new(process.InstanceId.ProcessId, 10, timestamp, operation, bytes);

    private static ProcessDescriptor Process(int pid, DateTimeOffset start) =>
        new(new ProcessInstanceId(pid, start), "test", null, null, null, null, null, null, false, true);

    private sealed class SequenceSource(params PhysicalDiskEventBatch[] batches) : IPhysicalDiskEventSource
    {
        private readonly Queue<PhysicalDiskEventBatch> _batches = new(batches);

        public ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_batches.Dequeue());
        }
    }

    private sealed class CancellingSource : IPhysicalDiskEventSource
    {
        public ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<PhysicalDiskEventBatch>(cancellationToken);
    }
}
