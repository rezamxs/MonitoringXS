namespace MonitoringXS.App.Tests;

public sealed class LiveRefreshLoopTests
{
    [Fact]
    public async Task TransientFaultRecoversWithoutOverlappingRefreshes()
    {
        using CancellationTokenSource shutdown = new();
        int calls = 0;
        int active = 0;
        int maximumActive = 0;
        List<Exception> faults = [];

        Task run = LiveRefreshLoop.RunAsync(
            async cancellationToken =>
            {
                int current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                try
                {
                    int call = Interlocked.Increment(ref calls);
                    await Task.Yield();
                    if (call == 1)
                    {
                        throw new InvalidOperationException("transient");
                    }
                    if (call == 4)
                    {
                        shutdown.Cancel();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            new LiveRefreshCadence(TimeSpan.FromMilliseconds(1)),
            faults.Add,
            shutdown.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(4, calls);
        Assert.Equal(1, maximumActive);
        Assert.Single(faults);
    }

    [Fact]
    public async Task CanceledHistoryQueryAndNavigationDoNotCancelLiveLoop()
    {
        using CancellationTokenSource shutdown = new();
        using CancellationTokenSource historyQuery = new();
        historyQuery.Cancel();
        int calls = 0;
        bool historyVisible = false;

        Task run = LiveRefreshLoop.RunAsync(
            cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                Assert.True(historyQuery.IsCancellationRequested);
                historyVisible = !historyVisible;
                if (Interlocked.Increment(ref calls) == 4)
                {
                    shutdown.Cancel();
                }
                return Task.CompletedTask;
            },
            new LiveRefreshCadence(TimeSpan.FromMilliseconds(1)),
            _ => throw new Xunit.Sdk.XunitException("No live-loop fault expected."),
            shutdown.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(4, calls);
        Assert.False(historyVisible);
    }

    [Fact]
    public async Task RepeatedSnapshotsStillReachRenderCallback()
    {
        using CancellationTokenSource shutdown = new();
        List<DateTimeOffset> rendered = [];
        DateTimeOffset start = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        Task run = LiveRefreshLoop.RunAsync(
            cancellationToken =>
            {
                rendered.Add(start.AddSeconds(rendered.Count));
                if (rendered.Count == 10)
                {
                    shutdown.Cancel();
                }
                return Task.CompletedTask;
            },
            new LiveRefreshCadence(TimeSpan.FromMilliseconds(1)),
            _ => throw new Xunit.Sdk.XunitException("No live-loop fault expected."),
            shutdown.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(10, rendered.Count);
        Assert.Equal(10, rendered.Distinct().Count());
    }

    [Fact]
    public async Task PreCanceledLifetimeStopsWithoutStartingWork()
    {
        using CancellationTokenSource shutdown = new();
        shutdown.Cancel();
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LiveRefreshLoop.RunAsync(
                _ =>
                {
                    calls++;
                    return Task.CompletedTask;
                },
                new LiveRefreshCadence(TimeSpan.FromMilliseconds(1)),
                _ => { },
                shutdown.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CadenceChangesWakeCurrentWaitAndRemainSingleExecution()
    {
        using CancellationTokenSource shutdown = new();
        LiveRefreshCadence cadence = new(TimeSpan.FromMinutes(1));
        int calls = 0;
        int active = 0;
        int maximumActive = 0;
        TaskCompletionSource firstCapture = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = LiveRefreshLoop.RunAsync(
            async cancellationToken =>
            {
                int current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                try
                {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 1)
                    {
                        firstCapture.SetResult();
                    }
                    await Task.Delay(5, cancellationToken);
                    if (call == 3)
                    {
                        shutdown.Cancel();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            cadence,
            _ => throw new Xunit.Sdk.XunitException("No live-loop fault expected."),
            shutdown.Token);

        await firstCapture.Task.WaitAsync(TestContext.Current.CancellationToken);
        cadence.Update(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(3, calls);
        Assert.Equal(1, maximumActive);
        Assert.Equal(TimeSpan.FromMilliseconds(1), cadence.Interval);
    }
}
