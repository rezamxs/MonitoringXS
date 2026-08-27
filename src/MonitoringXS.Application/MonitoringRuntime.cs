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
    private TaskCompletionSource? _activeRequest;
    private RuntimeState _state;
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
            if (_state is RuntimeState.Stopped or RuntimeState.Faulted)
            {
                throw new InvalidOperationException("A stopped monitoring runtime cannot be restarted.");
            }

            StartedAt ??= DateTimeOffset.UtcNow;
            _state = RuntimeState.Started;
            if (_loop is null)
            {
                _loop = RunAsync(_shutdown.Token);
                _ = _loop.ContinueWith(
                    completed =>
                    {
                        if (completed.IsFaulted)
                        {
                            lock (_gate)
                            {
                                _state = RuntimeState.Faulted;
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return _loop;
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
                return _state == RuntimeState.Started && !_disposed;
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
            if (_state != RuntimeState.Started)
            {
                throw new InvalidOperationException("The monitoring runtime is not running.");
            }

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
            _activeRequest?.TrySetCanceled(_shutdown.Token);
            _activeRequest = null;
            if (_state != RuntimeState.Faulted)
            {
                _state = RuntimeState.Stopped;
            }
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
        finally
        {
            lock (_gate)
            {
                _loop = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource? cycleRequest;
            lock (_gate)
            {
                cycleRequest = _requestedCapture;
                _requestedCapture = null;
                _activeRequest = cycleRequest;
            }

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
                cycleRequest?.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                cycleRequest?.TrySetException(exception);
                Trace.TraceError(
                    "Monitoring cycle failed ({0}); sampling will continue.",
                    exception.GetType().Name);
            }

            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeRequest, cycleRequest))
                    {
                        _activeRequest = null;
                    }
                }
            }

            await _cadence.WaitForNextCaptureAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private enum RuntimeState
    {
        Created,
        Started,
        Stopped,
        Faulted
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
