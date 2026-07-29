using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class HistoryPageViewModel : ObservableObject, IDisposable
{
    private static readonly HistoryMetricDefinition[] Definitions =
    [
        new(MetricHistoryMetric.CpuPercent, "CPU", HistoryValueKind.Percent, "%", true),
        new(MetricHistoryMetric.WorkingSetBytes, "Working Set memory", HistoryValueKind.Bytes, "bytes"),
        new(MetricHistoryMetric.ProcessIoReadBytesPerSecond, "Process I/O read", HistoryValueKind.BytesPerSecond, "bytes/s"),
        new(MetricHistoryMetric.ProcessIoWriteBytesPerSecond, "Process I/O write", HistoryValueKind.BytesPerSecond, "bytes/s"),
        new(MetricHistoryMetric.PhysicalDiskReadBytesPerSecond, "Physical Disk read", HistoryValueKind.BytesPerSecond, "bytes/s"),
        new(MetricHistoryMetric.PhysicalDiskWriteBytesPerSecond, "Physical Disk write", HistoryValueKind.BytesPerSecond, "bytes/s"),
        new(MetricHistoryMetric.NetworkDownloadBytesPerSecond, "Network receive", HistoryValueKind.BytesPerSecond, "bytes/s"),
        new(MetricHistoryMetric.NetworkUploadBytesPerSecond, "Network send", HistoryValueKind.BytesPerSecond, "bytes/s"),
        new(MetricHistoryMetric.GpuUtilizationPercent, "GPU utilization", HistoryValueKind.Percent, "%", true),
        new(MetricHistoryMetric.GpuDedicatedMemoryBytes, "Dedicated GPU memory", HistoryValueKind.Bytes, "bytes"),
        new(MetricHistoryMetric.GpuSharedMemoryBytes, "Shared GPU memory", HistoryValueKind.Bytes, "bytes")
    ];
    private readonly IMetricHistoryStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _debounce;
    private readonly int _maximumPoints;
    private CancellationTokenSource? _activeRequest;
    private int _requestVersion;
    private bool _disposed;

    public HistoryPageViewModel(IMetricHistoryStore store)
        : this(store, () => DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(180), 360)
    {
    }

    internal HistoryPageViewModel(
        IMetricHistoryStore store,
        Func<DateTimeOffset> utcNow,
        TimeSpan debounce,
        int maximumPoints)
    {
        _store = store;
        _utcNow = utcNow;
        _debounce = debounce;
        _maximumPoints = maximumPoints;
        Charts = Definitions.Select(definition => new HistoryMetricSeries(definition)).ToArray();
    }

    public ObservableCollection<MetricHistoryApplication> Applications { get; } = [];

    public IReadOnlyList<HistoryRangeOption> Ranges { get; } =
    [
        new("15 minutes", TimeSpan.FromMinutes(15)),
        new("1 hour", TimeSpan.FromHours(1)),
        new("6 hours", TimeSpan.FromHours(6)),
        new("24 hours", TimeSpan.FromHours(24))
    ];

    public IReadOnlyList<HistoryMetricSeries> Charts { get; }

    [ObservableProperty]
    public partial MetricHistoryApplication? SelectedApplication { get; set; }

    [ObservableProperty]
    public partial HistoryRangeOption SelectedRange { get; set; } =
        new("1 hour", TimeSpan.FromHours(1));

    [ObservableProperty]
    public partial HistoryPageState State { get; set; } = HistoryPageState.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Open History to load saved metrics.";

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; } = "Not loaded";

    [ObservableProperty]
    public partial string SelectedRangeText { get; set; } = "Selected range: 1 hour";

    [ObservableProperty]
    public partial string QueryPerformanceText { get; set; } = "";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        State = HistoryPageState.Loading;
        IsLoading = true;
        StatusText = "Loading saved applications…";
        try
        {
            MetricHistoryApplicationsResult result = await Task.Run(
                async () => await _store.ListApplicationsAsync(cancellationToken),
                cancellationToken);
            Applications.Clear();
            foreach (MetricHistoryApplication application in result.Applications)
            {
                Applications.Add(application);
            }

            if (!result.IsAvailable)
            {
                State = HistoryPageState.DatabaseUnavailable;
                StatusText = result.Error ?? "History database unavailable.";
                return;
            }

            if (Applications.Count == 0)
            {
                State = HistoryPageState.Empty;
                StatusText = "No saved application history yet.";
                return;
            }

            SelectedApplication ??= Applications[0];
            await LoadAsync(debounce: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State = HistoryPageState.Cancelled;
            StatusText = "History request cancelled.";
        }
        catch
        {
            State = HistoryPageState.QueryError;
            StatusText = "History query failed.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task SelectApplicationAsync(
        MetricHistoryApplication? application,
        CancellationToken cancellationToken)
    {
        SelectedApplication = application;
        return LoadAsync(debounce: true, cancellationToken);
    }

    public Task SelectRangeAsync(
        HistoryRangeOption range,
        CancellationToken cancellationToken)
    {
        SelectedRange = range;
        SelectedRangeText = $"Selected range: {range.Label}";
        return LoadAsync(debounce: true, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        LoadAsync(debounce: false, cancellationToken);

    private async Task LoadAsync(bool debounce, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int version = Interlocked.Increment(ref _requestVersion);
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _activeRequest, request);
        previous?.Cancel();
        previous?.Dispose();
        MetricHistoryApplication? application = SelectedApplication;
        HistoryRangeOption range = SelectedRange;
        IsLoading = true;
        State = HistoryPageState.Loading;
        StatusText = application is null ? "Application not found." : $"Loading {application.DisplayName}…";
        try
        {
            if (application is null)
            {
                State = HistoryPageState.ApplicationNotFound;
                return;
            }

            if (debounce)
            {
                await Task.Delay(_debounce, request.Token);
            }

            DateTimeOffset toUtc = _utcNow().ToUniversalTime();
            DateTimeOffset fromUtc = toUtc - range.Duration;
            Stopwatch stopwatch = Stopwatch.StartNew();
            MetricHistoryQueryResult[] results = await Task.Run(
                async () => await Task.WhenAll(Definitions.Select(definition =>
                    _store.QueryAsync(
                        application.LogicalApplicationId,
                        definition.Metric,
                        fromUtc,
                        toUtc,
                        request.Token).AsTask())),
                request.Token);
            var presentations = await Task.Run(
                () => Definitions
                    .Select((definition, index) => HistorySeriesPresentation.Create(
                        definition,
                        results[index],
                        range,
                        _maximumPoints))
                    .ToArray(),
                request.Token);
            stopwatch.Stop();
            if (version != Volatile.Read(ref _requestVersion))
            {
                return;
            }

            bool databaseUnavailable = results.Any(result => !result.IsAvailable);
            bool anyPoints = results.Any(result => result.Points.Count > 0);
            bool partial = results.Any(result => result.Points.Any(point =>
                point.Availability != MetricAvailability.Available));
            for (int index = 0; index < Charts.Count; index++)
            {
                Charts[index].Samples = presentations[index].Samples;
                Charts[index].Summary = presentations[index].Summary;
                Charts[index].StateText = presentations[index].State;
                Charts[index].AccessibilityText = presentations[index].Accessibility;
                Charts[index].RangeStartUtc = fromUtc;
                Charts[index].RangeEndUtc = toUtc;
            }

            State = databaseUnavailable
                ? HistoryPageState.DatabaseUnavailable
                : anyPoints
                    ? HistoryPageState.Ready
                    : HistoryPageState.Empty;
            StatusText = databaseUnavailable
                ? "History database unavailable."
                : anyPoints
                    ? partial
                        ? "History loaded with partial or unavailable metric gaps."
                        : "History loaded."
                    : "No history in the selected range.";
            DateTimeOffset localUpdated = toUtc.ToLocalTime();
            LastUpdatedText = $"Last updated {localUpdated:g}";
            SelectedRangeText = $"Selected range: {range.Label}";
            QueryPerformanceText = $"{stopwatch.Elapsed.TotalMilliseconds:0.0} ms · {Charts.Sum(chart => chart.Samples.Count)} chart points";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (version == Volatile.Read(ref _requestVersion)
                && cancellationToken.IsCancellationRequested)
            {
                State = HistoryPageState.Cancelled;
                StatusText = "History request cancelled.";
            }
        }
        catch
        {
            if (version == Volatile.Read(ref _requestVersion))
            {
                State = HistoryPageState.QueryError;
                StatusText = "History query failed.";
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _requestVersion))
            {
                IsLoading = false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _activeRequest, null, request), request))
            {
                request.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? active = Interlocked.Exchange(ref _activeRequest, null);
        active?.Cancel();
        active?.Dispose();
    }
}
