namespace MonitoringXS.Application;

#pragma warning disable CA1001 // SemaphoreSlim never exposes a wait handle and lives for the app lifetime.
public sealed class LiveRefreshCadence
{
    /// <summary>
    /// Slowest cadence used while no live view is on screen (window minimized or
    /// hidden). History keeps recording at this rate; a slower user choice wins.
    /// </summary>
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly object _gate = new();
    private long _requestedTicks;
    private long _intervalTicks;
    private bool _background;
    private bool _captureRequested;

    public LiveRefreshCadence(TimeSpan interval)
    {
        Update(interval);
        _changed.Wait(0, CancellationToken.None);
    }

    /// <summary>The interval the capture loop currently waits between ticks.</summary>
    public TimeSpan Interval => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));

    /// <summary>The user's chosen live sampling interval, independent of background throttling.</summary>
    public TimeSpan RequestedInterval => TimeSpan.FromTicks(Interlocked.Read(ref _requestedTicks));

    /// <summary>True while the window is not showing live data and sampling is throttled.</summary>
    public bool IsBackground
    {
        get
        {
            lock (_gate)
            {
                return _background;
            }
        }
    }

    public void Update(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        lock (_gate)
        {
            Interlocked.Exchange(ref _requestedTicks, interval.Ticks);
            ApplyEffectiveInterval();
        }
    }

    /// <summary>
    /// Switches between the user's interval (foreground) and the slower background
    /// interval. Returning to the foreground wakes the loop so it re-plans its wait.
    /// </summary>
    public void SetBackground(bool background)
    {
        lock (_gate)
        {
            if (_background == background)
            {
                return;
            }

            _background = background;
            ApplyEffectiveInterval();
        }
    }

    /// <summary>The interval to sample at for a requested interval and window presence.</summary>
    public static TimeSpan EffectiveInterval(TimeSpan requested, bool background) =>
        background && requested < BackgroundInterval ? BackgroundInterval : requested;

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

    private void ApplyEffectiveInterval()
    {
        TimeSpan effective = EffectiveInterval(
            TimeSpan.FromTicks(Interlocked.Read(ref _requestedTicks)),
            _background);
        Interlocked.Exchange(ref _intervalTicks, effective.Ticks);
        Signal();
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
