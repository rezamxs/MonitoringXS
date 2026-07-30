namespace MonitoringXS.App;

internal static class LiveRefreshLoop
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> refreshAsync,
        LiveRefreshCadence cadence,
        Action<Exception> onFault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshAsync);
        ArgumentNullException.ThrowIfNull(cadence);
        ArgumentNullException.ThrowIfNull(onFault);

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await refreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                onFault(exception);
            }
        }
        while (await cadence.WaitForNextCaptureAsync(cancellationToken));
    }
}

#pragma warning disable CA1001 // SemaphoreSlim never exposes a wait handle and lives for the app lifetime.
public sealed class LiveRefreshCadence
{
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly object _gate = new();
    private long _intervalTicks;

    public LiveRefreshCadence(TimeSpan interval)
    {
        Update(interval);
        _changed.Wait(0, CancellationToken.None);
    }

    public TimeSpan Interval => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));

    public void Update(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        lock (_gate)
        {
            Interlocked.Exchange(ref _intervalTicks, interval.Ticks);
            if (_changed.CurrentCount == 0)
            {
                _changed.Release();
            }
        }
    }

    public async ValueTask<bool> WaitForNextCaptureAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan interval = Interval;
            bool changed = await _changed
                .WaitAsync(interval, cancellationToken)
                .ConfigureAwait(false);
            if (!changed)
            {
                return true;
            }

            while (_changed.Wait(0, CancellationToken.None))
            {
            }
        }
    }
}
#pragma warning restore CA1001
