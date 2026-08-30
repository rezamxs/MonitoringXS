using System.Diagnostics;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application.Tests;

public sealed class MonitoringCapturePipelineTests
{
    [Fact]
    public async Task NonCooperativeStageCannotHoldPipelinePastHardDeadline()
    {
        TaskCompletionSource<MetricCaptureContribution> never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        NonCooperativeStage stage = new(never.Task);
        MonitoringCapturePipeline pipeline = new([stage]);
        Stopwatch elapsed = Stopwatch.StartNew();

        MonitoringMetricCaptureResult result = await pipeline.CaptureAsync(
            new(DateTimeOffset.UtcNow, [], []),
            [],
            TestContext.Current.CancellationToken);
        never.TrySetResult(new NetworkMetricContribution(new Dictionary<string, NetworkMetricSet>(), null));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        Assert.NotNull(stage.Failure);
        Assert.Empty(result.Applications);
    }

    [Fact]
    public async Task PipelineIsolatesSlowStageAndAllowsNextCycleToRun()
    {
        TaskCompletionSource<MetricCaptureContribution> cycle1Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MetricCaptureContribution> cycle2Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue<Task<MetricCaptureContribution>> tasks = new([cycle1Tcs.Task, cycle2Tcs.Task]);

        SequentialStage networkStage = new(MetricFamily.Network, tasks);
        List<MetricFamily> started = [];
        RecordingStage diskStage = new(MetricFamily.PhysicalDisk, started);

        MonitoringCapturePipeline pipeline = new([diskStage, networkStage]);
        Stopwatch elapsed = Stopwatch.StartNew();

        // Cycle 1: Network stage hangs past timeout. Disk stage completes.
        MonitoringMetricCaptureResult cycle1 = await pipeline.CaptureAsync(
            new(DateTimeOffset.UtcNow, [], []),
            [],
            TestContext.Current.CancellationToken);
        elapsed.Stop();

        // 1. Pipeline returns promptly (~750ms hard timeout)
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(700));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));

        // 2. Disk stage ran and was recorded
        Assert.Contains(MetricFamily.PhysicalDisk, started);

        // 3. Network stage failed with TimeoutException
        Assert.IsType<TimeoutException>(networkStage.LastFailure);

        // 4. The underlying cycle 1 task is STILL active/incomplete when cycle 1 finished
        Assert.False(cycle1Tcs.Task.IsCompleted);

        // Cycle 2: Network stage now returns immediately
        cycle2Tcs.TrySetResult(new NetworkMetricContribution(new Dictionary<string, NetworkMetricSet>(), null));

        MonitoringMetricCaptureResult cycle2 = await pipeline.CaptureAsync(
            new(DateTimeOffset.UtcNow, [], []),
            [],
            TestContext.Current.CancellationToken);

        // 5. Subsequent cycle succeeded and received the contribution
        Assert.NotNull(cycle2);

        // 6. Complete cycle 1 task late to verify clean resolution without unobserved fault
        cycle1Tcs.TrySetResult(new NetworkMetricContribution(new Dictionary<string, NetworkMetricSet>(), null));
        await cycle1Tcs.Task;
    }

    [Fact]
    public void DuplicateMetricFamilyIsRejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new MonitoringCapturePipeline([
                new RecordingStage(MetricFamily.Network, []),
                new RecordingStage(MetricFamily.Network, [])
            ]));

        Assert.Contains(nameof(MetricFamily.Network), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagesStartInDeterministicFamilyOrder()
    {
        List<MetricFamily> started = [];
        MonitoringCapturePipeline pipeline = new([
            new RecordingStage(MetricFamily.Gpu, started),
            new RecordingStage(MetricFamily.PhysicalDisk, started),
            new RecordingStage(MetricFamily.Network, started)
        ]);

        await pipeline.CaptureAsync(
            new(DateTimeOffset.UtcNow, [], []),
            [],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [MetricFamily.PhysicalDisk, MetricFamily.Network, MetricFamily.Gpu],
            started);
    }

    [Fact]
    public async Task RootCancellationIsNotConvertedToUnavailableMetrics()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        MonitoringCapturePipeline pipeline = new([
            new CancelingStage()
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.CaptureAsync(
                new(DateTimeOffset.UtcNow, [], []),
                [],
                cancellation.Token));
    }

    [Fact]
    public async Task StageDurationsAreRecordedPerFamily()
    {
        MonitoringCapturePipeline pipeline = new([
            new RecordingStage(MetricFamily.Network, [])
        ]);

        MonitoringMetricCaptureResult result = await pipeline.CaptureAsync(
            new(DateTimeOffset.UtcNow, [], []),
            [],
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.StageDurations);
        Assert.True(result.StageDurations.TryGetValue(MetricFamily.Network, out TimeSpan duration));
        Assert.True(duration >= TimeSpan.Zero);
        Assert.False(result.StageDurations.ContainsKey(MetricFamily.PhysicalDisk));
    }

    private sealed class RecordingStage(
        MetricFamily family,
        List<MetricFamily> started) : IMetricCaptureStage
    {
        public MetricFamily Family => family;

        public ValueTask<MetricCaptureContribution> CaptureAsync(
            MetricCaptureContext context,
            CancellationToken cancellationToken)
        {
            started.Add(Family);
            return ValueTask.FromResult(Contribution(Family));
        }

        public MetricCaptureContribution Failed(Exception exception) => Contribution(Family);
    }

    private sealed class CancelingStage : IMetricCaptureStage
    {
        public MetricFamily Family => MetricFamily.Network;

        public ValueTask<MetricCaptureContribution> CaptureAsync(
            MetricCaptureContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<MetricCaptureContribution>(cancellationToken);

        public MetricCaptureContribution Failed(Exception exception) =>
            Contribution(Family);
    }

    private sealed class NonCooperativeStage(Task<MetricCaptureContribution> capture) : IMetricCaptureStage
    {
        public Exception? Failure { get; private set; }

        public MetricFamily Family => MetricFamily.Network;

        public async ValueTask<MetricCaptureContribution> CaptureAsync(
            MetricCaptureContext context,
            CancellationToken cancellationToken) => await capture;

        public MetricCaptureContribution Failed(Exception exception)
        {
            Failure = exception;
            return new NetworkMetricContribution(new Dictionary<string, NetworkMetricSet>(), null);
        }
    }

    private sealed class SequentialStage(
        MetricFamily family,
        Queue<Task<MetricCaptureContribution>> queue) : IMetricCaptureStage
    {
        public Exception? LastFailure { get; private set; }
        public MetricFamily Family => family;

        public async ValueTask<MetricCaptureContribution> CaptureAsync(
            MetricCaptureContext context,
            CancellationToken cancellationToken)
        {
            if (queue.TryDequeue(out Task<MetricCaptureContribution>? task))
            {
                return await task;
            }
            return Contribution(Family);
        }

        public MetricCaptureContribution Failed(Exception exception)
        {
            LastFailure = exception;
            return Contribution(Family);
        }
    }

    private static MetricCaptureContribution Contribution(MetricFamily family) => family switch
    {
        MetricFamily.PhysicalDisk => new PhysicalDiskMetricContribution(
            new Dictionary<string, PhysicalDiskMetricSet>(StringComparer.Ordinal),
            null),
        MetricFamily.Network => new NetworkMetricContribution(
            new Dictionary<string, NetworkMetricSet>(StringComparer.Ordinal),
            null),
        _ => new GpuMetricContribution(
            new Dictionary<string, GpuMetricSet>(StringComparer.Ordinal),
            null)
    };
}
