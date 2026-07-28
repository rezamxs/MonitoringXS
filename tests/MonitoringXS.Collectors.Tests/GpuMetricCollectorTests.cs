using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class GpuMetricCollectorTests
{
    [Fact]
    public async Task UsesBusiestEngineForProcessUtilization()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(100, now.AddMinutes(-1));
        GpuMetricCollector collector = new(new SequenceSource(Batch(
            process,
            now,
            MetricAvailability.Available,
            [
                Engine(1, 0, 0, "3D", 25),
                Engine(1, 0, 1, "Copy", 70)
            ],
            128,
            64)));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(70d, sample.UtilizationPercent.Value);
        Assert.Equal(128UL, sample.DedicatedMemoryBytes.Value);
        Assert.Equal(64UL, sample.SharedMemoryBytes.Value);
    }

    [Fact]
    public async Task HealthyProcessWithoutInstancesIsMeasuredZero()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(101, now.AddMinutes(-1));
        GpuMetricCollector collector = new(new SequenceSource(Batch(
            process,
            now,
            MetricAvailability.Available,
            [],
            0,
            0)));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Available, sample.UtilizationPercent.Availability);
        Assert.Equal(0d, sample.UtilizationPercent.Value);
    }

    [Fact]
    public async Task IncompleteEngineSetIsPartialLowerBound()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(102, now.AddMinutes(-1));
        GpuMetricCollector collector = new(new SequenceSource(Batch(
            process,
            now,
            MetricAvailability.Partial,
            [Engine(1, 0, 0, "3D", 30)],
            128,
            64,
            "One engine was invalid.")));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Partial, sample.UtilizationPercent.Availability);
        Assert.Equal(30d, sample.UtilizationPercent.Value);
        Assert.Contains("invalid", sample.UtilizationPercent.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(100.1)]
    public async Task InvalidEngineValueIsNeverPublishedAsAvailable(double invalidValue)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(106, now.AddMinutes(-1));
        GpuMetricCollector collector = new(new SequenceSource(Batch(
            process,
            now,
            MetricAvailability.Available,
            [Engine(1, 0, 0, "3D", invalidValue)],
            128,
            64)));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.False(sample.UtilizationPercent.IsAvailable);
        Assert.NotEqual(MetricAvailability.Available, sample.UtilizationPercent.Availability);
    }

    [Fact]
    public async Task DuplicateEngineIdentityIsNotDoubleCounted()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(107, now.AddMinutes(-1));
        GpuMetricCollector collector = new(new SequenceSource(Batch(
            process,
            now,
            MetricAvailability.Available,
            [
                Engine(1, 0, 0, "3D", 25),
                Engine(1, 0, 0, "3D", 25)
            ],
            128,
            64)));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(25d, sample.UtilizationPercent.Value);
        Assert.Equal(MetricAvailability.Partial, sample.UtilizationPercent.Availability);
        Assert.Single(sample.Engines);
    }

    [Fact]
    public async Task UtilizationCanRemainAvailableWhenMemoryCountersAreUnavailable()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(108, now.AddMinutes(-1));
        GpuCounterBatch batch = new(
            [
                new GpuProcessCounterSnapshot(
                    process.InstanceId,
                    now,
                    [Engine(1, 0, 0, "3D", 20)],
                    MetricValue<ulong>.Unavailable(
                        MetricAvailability.Unsupported,
                        "Dedicated memory counter unavailable."),
                    MetricValue<ulong>.Unavailable(
                        MetricAvailability.Unavailable,
                        "Shared memory counter returned no data."),
                    MetricAvailability.Available)
            ],
            MetricAvailability.Partial,
            GpuAvailabilityReason.CounterUnavailable,
            new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Partial,
                Reason = GpuAvailabilityReason.CounterUnavailable,
                UtilizationCounterStatus = MetricAvailability.Available,
                DedicatedMemoryCounterStatus = MetricAvailability.Unsupported,
                SharedMemoryCounterStatus = MetricAvailability.Unavailable
            });
        GpuMetricCollector collector = new(new SequenceSource(batch));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Available, sample.UtilizationPercent.Availability);
        Assert.Equal(20d, sample.UtilizationPercent.Value);
        Assert.Equal(MetricAvailability.Unsupported, sample.DedicatedMemoryBytes.Availability);
        Assert.Equal(MetricAvailability.Unavailable, sample.SharedMemoryBytes.Availability);
    }

    [Fact]
    public async Task MissingEngineCounterDataIsNotConvertedToHealthyZero()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(109, now.AddMinutes(-1));
        GpuCounterBatch batch = new(
            [
                new GpuProcessCounterSnapshot(
                    process.InstanceId,
                    now,
                    [],
                    MetricValue<ulong>.Available(0),
                    MetricValue<ulong>.Available(0),
                    MetricAvailability.Unavailable,
                    "GPU engine counter returned no data.")
            ],
            MetricAvailability.Partial,
            GpuAvailabilityReason.CounterUnavailable,
            new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Partial,
                Reason = GpuAvailabilityReason.CounterUnavailable
            });
        GpuMetricCollector collector = new(new SequenceSource(batch));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Unavailable, sample.UtilizationPercent.Availability);
        Assert.Null(sample.UtilizationPercent.Value);
        Assert.Equal(0UL, sample.DedicatedMemoryBytes.Value);
    }

    [Fact]
    public async Task QuarantinedCounterFamiliesRemainUnavailableIndependently()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(110, now.AddMinutes(-1));
        GpuCounterBatch batch = new(
            [
                new GpuProcessCounterSnapshot(
                    process.InstanceId,
                    now,
                    [],
                    MetricValue<ulong>.Unavailable(
                        MetricAvailability.Unavailable,
                        "Dedicated GPU memory is quarantined."),
                    MetricValue<ulong>.Available(4096),
                    MetricAvailability.Unavailable,
                    "GPU utilization is quarantined.")
            ],
            MetricAvailability.Partial,
            GpuAvailabilityReason.AmbiguousCounterLifetime,
            new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Partial,
                Reason = GpuAvailabilityReason.AmbiguousCounterLifetime,
                QuarantinedUtilizationSamples = 1,
                QuarantinedDedicatedMemorySamples = 1,
                SharedMemoryCounterStatus = MetricAvailability.Available
            });
        GpuMetricCollector collector = new(new SequenceSource(batch));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Unavailable, sample.UtilizationPercent.Availability);
        Assert.Null(sample.UtilizationPercent.Value);
        Assert.Equal(MetricAvailability.Unavailable, sample.DedicatedMemoryBytes.Availability);
        Assert.Null(sample.DedicatedMemoryBytes.Value);
        Assert.Equal(MetricAvailability.Available, sample.SharedMemoryBytes.Availability);
        Assert.Equal(4096UL, sample.SharedMemoryBytes.Value);
    }

    [Theory]
    [InlineData(MetricAvailability.WarmingUp)]
    [InlineData(MetricAvailability.AccessDenied)]
    [InlineData(MetricAvailability.Unsupported)]
    public async Task ProviderAvailabilityIsNotConvertedToZero(MetricAvailability availability)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(103, now.AddMinutes(-1));
        GpuCounterBatch batch = new(
            [],
            availability,
            GpuAvailabilityReason.CounterSetUnavailable,
            new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = availability,
                Reason = GpuAvailabilityReason.CounterSetUnavailable,
                CollectorStatusReason = "Unavailable."
            });
        GpuMetricCollector collector = new(new SequenceSource(batch));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [process],
            now,
            CancellationToken.None));

        Assert.Equal(availability, sample.UtilizationPercent.Availability);
        Assert.Null(sample.UtilizationPercent.Value);
    }

    [Fact]
    public async Task SamePidWithDifferentStartTimeIsNeverAccepted()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor current = Process(105, now.AddMinutes(-1));
        ProcessDescriptor stale = Process(105, now.AddMinutes(-2));
        GpuMetricCollector collector = new(new SequenceSource(Batch(
            stale,
            now,
            MetricAvailability.Available,
            [Engine(1, 0, 0, "3D", 80)],
            128,
            64)));

        GpuProcessSample sample = Assert.Single(await collector.CollectAsync(
            [current],
            now,
            CancellationToken.None));

        Assert.Equal(MetricAvailability.Error, sample.UtilizationPercent.Availability);
        Assert.Null(sample.UtilizationPercent.Value);
    }

    [Fact]
    public async Task CancellationIsPreserved()
    {
        ProcessDescriptor process = Process(104, DateTimeOffset.UtcNow.AddMinutes(-1));
        GpuMetricCollector collector = new(new CancellingSource());

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await collector.CollectAsync(
                [process],
                DateTimeOffset.UtcNow,
                new CancellationToken(canceled: true)));
    }

    private static GpuCounterBatch Batch(
        ProcessDescriptor process,
        DateTimeOffset now,
        MetricAvailability availability,
        IReadOnlyList<GpuEngineUsage> engines,
        ulong dedicated,
        ulong shared,
        string? detail = null) => new(
        [
            new GpuProcessCounterSnapshot(
                process.InstanceId,
                now,
                engines,
                availability == MetricAvailability.Partial
                    ? MetricValue<ulong>.Partial(dedicated, detail ?? "Partial.")
                    : MetricValue<ulong>.Available(dedicated),
                availability == MetricAvailability.Partial
                    ? MetricValue<ulong>.Partial(shared, detail ?? "Partial.")
                    : MetricValue<ulong>.Available(shared),
                availability,
                detail)
        ],
        availability,
        availability == MetricAvailability.Available
            ? GpuAvailabilityReason.None
            : GpuAvailabilityReason.CounterReadFailure,
        new GpuCollectorDiagnostics
        {
            ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
            CollectorStatus = availability,
            Reason = availability == MetricAvailability.Available
                ? GpuAvailabilityReason.None
                : GpuAvailabilityReason.CounterReadFailure
        });

    private static GpuEngineUsage Engine(
        ulong luid,
        int physical,
        int index,
        string type,
        double utilization) => new(
        new GpuEngineId(luid, physical, index, type),
        utilization);

    private static ProcessDescriptor Process(int processId, DateTimeOffset startTime) => new(
        new ProcessInstanceId(processId, startTime),
        "process",
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        false);

    private sealed class SequenceSource(params GpuCounterBatch[] batches) : IGpuCounterSource
    {
        private readonly Queue<GpuCounterBatch> _batches = new(batches);

        public ValueTask<GpuCounterBatch> CaptureAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_batches.Dequeue());
    }

    private sealed class CancellingSource : IGpuCounterSource
    {
        public ValueTask<GpuCounterBatch> CaptureAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<GpuCounterBatch>(cancellationToken);
    }
}
