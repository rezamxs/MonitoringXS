using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application.Tests;

public sealed class MonitoringRuntimeTests
{
    [Fact]
    public async Task StartIsIdempotentCyclesDoNotOverlapAndRecoverableFailureDoesNotStopLoop()
    {
        int calls = 0;
        int active = 0;
        int maximumActive = 0;
        TaskCompletionSource fourthCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MonitoringSnapshotHub hub = new();
        await using MonitoringRuntime runtime = new(
            async cancellationToken =>
            {
                int current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                try
                {
                    int call = Interlocked.Increment(ref calls);
                    await Task.Delay(2, cancellationToken);
                    if (call == 1)
                    {
                        throw new InvalidOperationException("transient");
                    }

                    if (call == 4)
                    {
                        fourthCall.TrySetResult();
                    }

                    return Snapshot(call);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            hub,
            new LiveRefreshCadence(TimeSpan.FromMilliseconds(1)));

        Task firstStart = runtime.Start();
        Task secondStart = runtime.Start();
        await fourthCall.Task.WaitAsync(TestContext.Current.CancellationToken);
        await runtime.StopAsync();

        Assert.Same(firstStart, secondStart);
        Assert.Equal(1, maximumActive);
        Assert.True(calls >= 4);
        Assert.NotNull(hub.Latest);
    }

    [Fact]
    public async Task CadenceUpdateWakesSingleLoop()
    {
        int calls = 0;
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveRefreshCadence cadence = new(TimeSpan.FromHours(1));
        await using MonitoringRuntime runtime = new(
            cancellationToken =>
            {
                int call = Interlocked.Increment(ref calls);
                (call == 1 ? first : second).TrySetResult();
                return ValueTask.FromResult(Snapshot(call));
            },
            new MonitoringSnapshotHub(),
            cadence);

        _ = runtime.Start();
        await first.Task.WaitAsync(TestContext.Current.CancellationToken);
        cadence.Update(TimeSpan.FromMilliseconds(1));
        await second.Task.WaitAsync(TestContext.Current.CancellationToken);
        await runtime.StopAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(1), cadence.Interval);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task StopCancelsActiveCaptureCleanly()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancellationObserved = false;
        await using MonitoringRuntime runtime = new(
            async cancellationToken =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Snapshot(1);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                    throw;
                }
            },
            new MonitoringSnapshotHub(),
            new LiveRefreshCadence(TimeSpan.FromHours(1)));

        _ = runtime.Start();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await runtime.StopAsync();

        Assert.True(cancellationObserved);
    }

    [Fact]
    public async Task EveryAcceptedSnapshotUsesExistingHistoryQueuePath()
    {
        RecordingHistoryStore history = new();
        MonitoringSnapshot accepted = Snapshot(1);
        TaskCompletionSource captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using MonitoringRuntime runtime = new(
            cancellationToken =>
            {
                captured.TrySetResult();
                return ValueTask.FromResult(accepted);
            },
            new MonitoringSnapshotHub(),
            new LiveRefreshCadence(TimeSpan.FromHours(1)),
            history);

        _ = runtime.Start();
        await captured.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => history.Enqueued == 1);
        await runtime.StopAsync();

        Assert.Equal(1, history.Enqueued);
        Assert.NotNull(history.Capture);
        Assert.Same(accepted.Discovery, history.Capture.Discovery);
        Assert.Equal(accepted.CapturedAt, history.Capture.ObservedAtUtc);
    }

    [Fact]
    public async Task RequestedCaptureCompletesAfterNextAcceptedSnapshot()
    {
        int calls = 0;
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MonitoringSnapshotHub hub = new();
        await using MonitoringRuntime runtime = new(
            cancellationToken =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    first.TrySetResult();
                }

                return ValueTask.FromResult(Snapshot(call));
            },
            hub,
            new LiveRefreshCadence(TimeSpan.FromHours(1)));

        _ = runtime.Start();
        await first.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => hub.Latest is not null);
        await runtime.RequestCaptureAsync(TestContext.Current.CancellationToken);
        await runtime.StopAsync();

        Assert.True(calls >= 2);
    }

    private static MonitoringSnapshot Snapshot(int sequence)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow.AddTicks(sequence);
        return new(
            capturedAt,
            new ProcessDiscoverySnapshot([], [], []),
            [],
            new Dictionary<string, IReadOnlyList<ApplicationHistoryPoint>>(StringComparer.Ordinal));
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class RecordingHistoryStore : IMetricHistoryStore
    {
        public int Enqueued;
        public MetricHistoryCapture? Capture { get; private set; }

        public MetricHistoryStoreDiagnostics Diagnostics => new(0, 0, 0, 0, 0, 0, 0, 0, 0, null);

        public ValueTask<MetricHistoryWriteResult> EnqueueAsync(
            IReadOnlyList<ApplicationMetricSnapshot> snapshots,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Enqueued);
            return ValueTask.FromResult(MetricHistoryWriteResult.Success);
        }

        public ValueTask<MetricHistoryWriteResult> EnqueueAsync(
            MetricHistoryCapture capture,
            CancellationToken cancellationToken)
        {
            Capture = capture;
            Interlocked.Increment(ref Enqueued);
            return ValueTask.FromResult(MetricHistoryWriteResult.Success);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<MetricHistoryApplicationsResult> ListApplicationsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricHistoryApplicationsResult([], true));

        public ValueTask<MetricHistoryQueryResult> QueryAsync(
            string logicalApplicationId,
            MetricHistoryMetric metric,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricHistoryQueryResult([], true));

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
