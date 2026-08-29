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
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(Available(), Available()), time);

        PhysicalDiskProcessSample first = Assert.Single(await collector.CollectAsync([process], now, CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(2));
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
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            Available(
                Event(process, now.AddMilliseconds(100), PhysicalDiskOperation.Read, 2048),
                Event(process, now.AddMilliseconds(200), PhysicalDiskOperation.Write, 4096))),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
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
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(Available(stale), Available()), time);

        PhysicalDiskProcessSample first = Assert.Single(await collector.CollectAsync([process], start.AddSeconds(1), CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
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
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            Available(new PhysicalDiskIoEvent(45, 10, eventWithUtcOffset, PhysicalDiskOperation.Read, 512))),
            time);

        await collector.CollectAsync([process], startWithOffset.AddSeconds(1), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
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
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            new PhysicalDiskEventBatch(
                [Event(process, now.AddMilliseconds(1), PhysicalDiskOperation.Read, 1024)],
                MetricAvailability.Available,
                2,
                0,
                0)),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
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
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 0, EventsObserved: 10, MaximumQueueDepth: 3, EtwBufferSizeMegabytes: 32),
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 0, EventsObserved: 30, CurrentQueueDepth: 2, MaximumQueueDepth: 7, EtwBufferSizeMegabytes: 32)),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
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
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 10),
            new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 20)),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(1),
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Available, sample.ReadBytesPerSecond.Availability);
        Assert.Equal(20, sample.Diagnostics.UnattributedEvents);
    }

    [Fact]
    public async Task RatesUseMonotonicElapsedWhenUtcClockMovesBackward()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(51, now.AddMinutes(-1));
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            Available(Event(process, now.AddMilliseconds(1), PhysicalDiskOperation.Read, 2000))),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddHours(-1),
            CancellationToken.None));

        Assert.Equal(1000d, sample.ReadBytesPerSecond.Value);
        Assert.Equal(MetricAvailability.Available, sample.ReadBytesPerSecond.Availability);
    }

    [Fact]
    public async Task NearZeroIntervalDoesNotSpikeAndCarriesBytesIntoNextRate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(52, now.AddMinutes(-1));
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            Available(Event(process, now.AddMilliseconds(1), PhysicalDiskOperation.Read, 1000)),
            Available(Event(process, now.AddMilliseconds(2), PhysicalDiskOperation.Read, 1000))),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(1));
        PhysicalDiskProcessSample tooSoon = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddMilliseconds(1),
            CancellationToken.None));
        time.Advance(TimeSpan.FromMilliseconds(999));
        PhysicalDiskProcessSample stable = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(1),
            CancellationToken.None));

        Assert.Equal(MetricAvailability.WarmingUp, tooSoon.ReadBytesPerSecond.Availability);
        Assert.Null(tooSoon.ReadBytesPerSecond.Value);
        Assert.Equal(2000d, stable.ReadBytesPerSecond.Value);
        Assert.Equal(2000UL, stable.SessionReadBytes.Value);
    }

    [Fact]
    public async Task UnknownPidEventIsIgnoredWithoutCrashingOrContaminatingTotals()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(53, now.AddMinutes(-1));
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(new PhysicalDiskIoEvent(9999, 12, now, PhysicalDiskOperation.Read, 4096)),
            Available()),
            time);

        PhysicalDiskProcessSample first = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample second = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(1),
            CancellationToken.None));

        Assert.Equal(0UL, first.SessionReadBytes.Value);
        Assert.Equal(0d, second.ReadBytesPerSecond.Value);
        Assert.Equal(0, second.Diagnostics.PidReuseEventsRejected);
        Assert.Equal(1, second.Diagnostics.UnattributedEvents);
    }

    [Fact]
    public async Task ProcessExitEvictsRateStateAndReentryWarmsUp()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(54, now.AddMinutes(-1));
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            Available(),
            Available(Event(process, now.AddSeconds(2), PhysicalDiskOperation.Read, 512))),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await collector.CollectAsync([], now.AddSeconds(1), CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample reentered = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(2),
            CancellationToken.None));

        Assert.Equal(MetricAvailability.WarmingUp, reentered.ReadBytesPerSecond.Availability);
        Assert.Equal(512UL, reentered.SessionReadBytes.Value);
    }

    [Fact]
    public async Task QueueOverflowMarksObservedValuesAsPartialLowerBounds()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(55, now.AddMinutes(-1));
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            new PhysicalDiskEventBatch(
                [Event(process, now.AddMilliseconds(1), PhysicalDiskOperation.Write, 2048)],
                MetricAvailability.Available,
                0,
                1,
                0)),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(1),
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Partial, sample.WriteBytesPerSecond.Availability);
        Assert.Equal(2048d, sample.WriteBytesPerSecond.Value);
        Assert.Contains("lower bounds", sample.WriteBytesPerSecond.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionTotalsRemainLowerBoundsAfterAConfirmedLoss()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(58, now.AddMinutes(-1));
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            new PhysicalDiskEventBatch(
                [Event(process, now.AddMilliseconds(1), PhysicalDiskOperation.Read, 1024)],
                MetricAvailability.Partial,
                1,
                0,
                0),
            Available()),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample lostInterval = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(1),
            CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample recoveredInterval = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(2),
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Partial, lostInterval.SessionReadBytes.Availability);
        Assert.Equal(MetricAvailability.Available, recoveredInterval.ReadBytesPerSecond.Availability);
        Assert.Equal(MetricAvailability.Partial, recoveredInterval.SessionReadBytes.Availability);
        Assert.True(recoveredInterval.Diagnostics.SessionTotalsAreLowerBounds);
        Assert.Equal(MetricAvailability.Partial, recoveredInterval.Diagnostics.CollectorStatus);
    }

    [Fact]
    public async Task UnavailableCollectorClearsRateStateBeforeRecovery()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(59, now.AddMinutes(-1));
        ManualTimeProvider time = new();
        PhysicalDiskMetricCollector collector = new(new SequenceSource(
            Available(),
            new PhysicalDiskEventBatch([], MetricAvailability.AccessDenied, 0, 0, 0, "denied"),
            Available()),
            time);

        await collector.CollectAsync([process], now, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample denied = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(1),
            CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1));
        PhysicalDiskProcessSample recovered = Assert.Single(await collector.CollectAsync(
            [process],
            now.AddSeconds(2),
            CancellationToken.None));

        Assert.Equal(MetricAvailability.AccessDenied, denied.ReadBytesPerSecond.Availability);
        Assert.Equal(MetricAvailability.WarmingUp, recovered.ReadBytesPerSecond.Availability);
        Assert.Equal(0UL, recovered.SessionReadBytes.Value);
    }

    [Fact]
    public async Task ExtendedDiagnosticsArePropagatedWithoutSyntheticValues()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(56, now.AddMinutes(-1));
        PhysicalDiskEventBatch batch = new(
            [],
            MetricAvailability.Available,
            2,
            3,
            4,
            EventsObserved: 11,
            ReadEventsObserved: 5,
            WriteEventsObserved: 6,
            ReadBytesObserved: 700,
            WriteBytesObserved: 800,
            MetadataLookupFailures: 9,
            SessionStartFailures: 10,
            AccessDeniedFailures: 1,
            LastSuccessfulEventTimestampUtc: now);
        PhysicalDiskMetricCollector collector = new(new SequenceSource(batch), new ManualTimeProvider());

        PhysicalDiskProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(5, sample.Diagnostics.ReadEventsObserved);
        Assert.Equal(6, sample.Diagnostics.WriteEventsObserved);
        Assert.Equal(700UL, sample.Diagnostics.ReadBytesObserved);
        Assert.Equal(800UL, sample.Diagnostics.WriteBytesObserved);
        Assert.Equal(9, sample.Diagnostics.MetadataLookupFailures);
        Assert.Equal(10, sample.Diagnostics.SessionStartFailures);
        Assert.Equal(1, sample.Diagnostics.AccessDeniedFailures);
        Assert.Equal(now, sample.Diagnostics.LastSuccessfulEventTimestampUtc);
        Assert.Equal(MetricAvailability.Partial, sample.Diagnostics.CollectorStatus);
    }

    [Fact]
    public async Task NewCollectorInstanceRestartsInWarmingUpState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(57, now.AddMinutes(-1));

        PhysicalDiskProcessSample first = Assert.Single(await new PhysicalDiskMetricCollector(
            new SequenceSource(Available()),
            new ManualTimeProvider()).CollectAsync([process], now, CancellationToken.None));
        PhysicalDiskProcessSample restarted = Assert.Single(await new PhysicalDiskMetricCollector(
            new SequenceSource(Available()),
            new ManualTimeProvider()).CollectAsync([process], now.AddSeconds(1), CancellationToken.None));

        Assert.Equal(MetricAvailability.WarmingUp, first.ReadBytesPerSecond.Availability);
        Assert.Equal(MetricAvailability.WarmingUp, restarted.ReadBytesPerSecond.Availability);
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

        public ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(
            IReadOnlyList<ProcessInstanceId> processes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_batches.Dequeue());
        }
    }

    private sealed class CancellingSource : IPhysicalDiskEventSource
    {
        public ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(
            IReadOnlyList<ProcessInstanceId> processes,
            CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<PhysicalDiskEventBatch>(cancellationToken);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = TimeSpan.TicksPerSecond;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
