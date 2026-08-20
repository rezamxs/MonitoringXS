using System.Diagnostics;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application;

public sealed class MonitoringRuntime : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<MonitoringSnapshot>> _captureAsync;
    private readonly IMetricHistoryStore? _historyStore;
    private readonly MonitoringSnapshotHub _hub;
    private readonly LiveRefreshCadence _cadence;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private Task? _loop;
    private TaskCompletionSource? _requestedCapture;
    private bool _disposed;

    public MonitoringRuntime(
        MonitoringCoordinator coordinator,
        MonitoringSnapshotHub hub,
        LiveRefreshCadence cadence,
        IMetricHistoryStore? historyStore = null)
        : this(coordinator.CaptureAsync, hub, cadence, historyStore)
    {
    }

    internal MonitoringRuntime(
        Func<CancellationToken, ValueTask<MonitoringSnapshot>> captureAsync,
        MonitoringSnapshotHub hub,
        LiveRefreshCadence cadence,
        IMetricHistoryStore? historyStore = null)
    {
        ArgumentNullException.ThrowIfNull(captureAsync);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(cadence);
        _captureAsync = captureAsync;
        _hub = hub;
        _cadence = cadence;
        _historyStore = historyStore;
    }

    public Task Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartedAt ??= DateTimeOffset.UtcNow;
            return _loop ??= RunAsync(_shutdown.Token);
        }
    }

    /// <summary>When the monitoring loop was first started, or null if it has not started.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>True while the monitoring loop is started and not disposed.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is not null && !_disposed;
            }
        }
    }

    public Task RequestCaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task requested;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requestedCapture ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            requested = _requestedCapture.Task;
        }

        _cadence.RequestCapture();
        return requested.WaitAsync(cancellationToken);
    }

    public async ValueTask StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _shutdown.Cancel();
            _requestedCapture?.TrySetCanceled(_shutdown.Token);
            _requestedCapture = null;
        }

        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                MonitoringSnapshot snapshot = await _captureAsync(cancellationToken);
                if (_historyStore is not null)
                {
                    try
                    {
                        await _historyStore.EnqueueAsync(
                            new MetricHistoryCapture(
                                snapshot.CapturedAt,
                                snapshot.Discovery,
                                snapshot.Applications),
                            cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        Trace.TraceError(
                            "History enqueue failed ({0}).",
                            exception.GetType().Name);
                    }
                }

                _hub.Publish(snapshot);
                TaskCompletionSource? requested;
                lock (_gate)
                {
                    requested = _requestedCapture;
                    _requestedCapture = null;
                }

                requested?.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceError(
                    "Monitoring cycle failed ({0}); sampling will continue.",
                    exception.GetType().Name);
            }

            await _cadence.WaitForNextCaptureAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
