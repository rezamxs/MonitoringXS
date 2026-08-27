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
