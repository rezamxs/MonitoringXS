namespace MonitoringXS.Application;

#pragma warning disable CA1001 // SemaphoreSlim never exposes a wait handle and lives for the app lifetime.
public sealed class LiveRefreshCadence
{
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly object _gate = new();
    private long _intervalTicks;
    private bool _captureRequested;

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
            Signal();
        }
    }

    public void RequestCapture()
    {
        lock (_gate)
        {
            _captureRequested = true;
            Signal();
        }
    }

    public async ValueTask WaitForNextCaptureAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan interval = Interval;
            bool changed = await _changed.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            if (!changed)
            {
                return;
            }

            lock (_gate)
            {
                if (_captureRequested)
                {
                    _captureRequested = false;
                    return;
                }
            }

            while (_changed.Wait(0, CancellationToken.None))
            {
            }
        }
    }

    private void Signal()
    {
        if (_changed.CurrentCount == 0)
        {
            _changed.Release();
        }
    }
}
#pragma warning restore CA1001
