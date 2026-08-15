using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.App.Localization;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class HistoryPageViewModel : ObservableObject, IDisposable
{
    private static readonly HistoryMetricDefinition[] Definitions =
    [
        new(MetricHistoryMetric.CpuPercent, LocalizationKeys.MetricCpu, HistoryValueKind.Percent, LocalizationKeys.UnitPercent, true),
        new(MetricHistoryMetric.WorkingSetBytes, LocalizationKeys.MetricWorkingSet, HistoryValueKind.Bytes, LocalizationKeys.UnitBytes),
        new(MetricHistoryMetric.ProcessIoReadBytesPerSecond, LocalizationKeys.MetricIoRead, HistoryValueKind.BytesPerSecond, LocalizationKeys.UnitBytesPerSecond),
        new(MetricHistoryMetric.ProcessIoWriteBytesPerSecond, LocalizationKeys.MetricIoWrite, HistoryValueKind.BytesPerSecond, LocalizationKeys.UnitBytesPerSecond),
        new(MetricHistoryMetric.PhysicalDiskReadBytesPerSecond, LocalizationKeys.MetricDiskRead, HistoryValueKind.BytesPerSecond, LocalizationKeys.UnitBytesPerSecond),
        new(MetricHistoryMetric.PhysicalDiskWriteBytesPerSecond, LocalizationKeys.MetricDiskWrite, HistoryValueKind.BytesPerSecond, LocalizationKeys.UnitBytesPerSecond),
        new(MetricHistoryMetric.NetworkDownloadBytesPerSecond, LocalizationKeys.MetricNetworkReceive, HistoryValueKind.BytesPerSecond, LocalizationKeys.UnitBytesPerSecond),
        new(MetricHistoryMetric.NetworkUploadBytesPerSecond, LocalizationKeys.MetricNetworkSend, HistoryValueKind.BytesPerSecond, LocalizationKeys.UnitBytesPerSecond),
        new(MetricHistoryMetric.GpuUtilizationPercent, LocalizationKeys.MetricGpuUtilization, HistoryValueKind.Percent, LocalizationKeys.UnitPercent, true),
        new(MetricHistoryMetric.GpuDedicatedMemoryBytes, LocalizationKeys.MetricGpuDedicated, HistoryValueKind.Bytes, LocalizationKeys.UnitBytes),
        new(MetricHistoryMetric.GpuSharedMemoryBytes, LocalizationKeys.MetricGpuShared, HistoryValueKind.Bytes, LocalizationKeys.UnitBytes)
    ];
    private readonly IMetricHistoryStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _debounce;
    private readonly int _maximumPoints;
    private readonly LocalizationService _localization;
    private CancellationTokenSource? _activeRequest;
    private int _requestVersion;
    private bool _disposed;

    public HistoryPageViewModel(
        IMetricHistoryStore store,
        LocalizationService? localization = null)
        : this(store, () => DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(180), 360, localization)
    {
    }

    internal HistoryPageViewModel(
        IMetricHistoryStore store,
        Func<DateTimeOffset> utcNow,
        TimeSpan debounce,
        int maximumPoints,
        LocalizationService? localization = null)
    {
        _store = store;
        _utcNow = utcNow;
        _debounce = debounce;
        _maximumPoints = maximumPoints;
        _localization = localization ?? new LocalizationService();
        Charts = Definitions.Select(definition => new HistoryMetricSeries(definition)).ToArray();
        foreach (HistoryMetricSeries chart in Charts)
        {
            chart.Relocalize(_localization);
        }
        BuildRanges();
        SelectedRange = Ranges.Single(range => range.Duration == TimeSpan.FromHours(1));
        _localization.LanguageChanged += Localization_LanguageChanged;
    }

    public ObservableCollection<MetricHistoryApplication> Applications { get; } = [];

    public IReadOnlyList<HistoryRangeOption> Ranges { get; private set; } = [];

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
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedRangeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string QueryPerformanceText { get; set; } = "";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        State = HistoryPageState.Loading;
        IsLoading = true;
        StatusText = _localization.Get(LocalizationKeys.HistoryLoading);
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
                StatusText = _localization.Get(LocalizationKeys.HistoryDatabaseUnavailable);
                return;
            }

            if (Applications.Count == 0)
            {
                State = HistoryPageState.Empty;
                StatusText = _localization.Get(LocalizationKeys.HistoryNoSaved);
                return;
            }

            SelectedApplication ??= Applications[0];
            await LoadAsync(debounce: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State = HistoryPageState.Cancelled;
            StatusText = _localization.Get(LocalizationKeys.HistoryCancelled);
        }
        catch
        {
            State = HistoryPageState.QueryError;
            StatusText = _localization.Get(LocalizationKeys.HistoryQueryFailed);
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
        SelectedRangeText = _localization.Format(LocalizationKeys.SelectedRangeFormat, range.Label);
        return LoadAsync(debounce: true, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        LoadAsync(debounce: false, cancellationToken);

    private void BuildRanges()
    {
        TimeSpan selected = SelectedRange.Duration;
        Ranges =
        [
            new(_localization.Get(LocalizationKeys.Range5Minutes), TimeSpan.FromMinutes(5)),
            new(_localization.Get(LocalizationKeys.Range15Minutes), TimeSpan.FromMinutes(15)),
            new(_localization.Get(LocalizationKeys.Range1Hour), TimeSpan.FromHours(1)),
            new(_localization.Get(LocalizationKeys.Range3Hours), TimeSpan.FromHours(3)),
            new(_localization.Get(LocalizationKeys.Range6Hours), TimeSpan.FromHours(6)),
            new(_localization.Get(LocalizationKeys.Range12Hours), TimeSpan.FromHours(12)),
            new(_localization.Get(LocalizationKeys.Range24Hours), TimeSpan.FromHours(24))
        ];
        SelectedRange = Ranges.First(item => item.Duration == selected);
        SelectedRangeText = _localization.Format(LocalizationKeys.SelectedRangeFormat, SelectedRange.Label);
        OnPropertyChanged(nameof(Ranges));
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs args)
    {
        BuildRanges();
        foreach (HistoryMetricSeries chart in Charts)
        {
            chart.Relocalize(_localization);
        }

        StatusText = State switch
        {
            HistoryPageState.Loading => _localization.Get(LocalizationKeys.HistoryLoading),
            HistoryPageState.DatabaseUnavailable => _localization.Get(LocalizationKeys.HistoryDatabaseUnavailable),
            HistoryPageState.Empty => _localization.Get(LocalizationKeys.HistoryNoRange),
            _ => StatusText
        };
        OnPropertyChanged(nameof(SelectedRangeText));
    }

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
        StatusText = application is null
            ? _localization.Get(LocalizationKeys.HistoryApplicationNotFound)
            : string.Format(_localization.Culture, "{0}…", application.DisplayName);
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
                        _maximumPoints,
                        _localization))
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
                ? _localization.Get(LocalizationKeys.HistoryDatabaseUnavailable)
                : anyPoints
                    ? partial
                        ? _localization.Get(LocalizationKeys.HistoryLoadedPartial)
                        : _localization.Get(LocalizationKeys.HistoryLoaded)
                    : _localization.Get(LocalizationKeys.HistoryNoRange);
            DateTimeOffset localUpdated = toUtc.ToLocalTime();
            LastUpdatedText = $"Last updated {localUpdated:g}";
            SelectedRangeText = _localization.Format(LocalizationKeys.SelectedRangeFormat, range.Label);
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
        _localization.LanguageChanged -= Localization_LanguageChanged;
        CancellationTokenSource? active = Interlocked.Exchange(ref _activeRequest, null);
        active?.Cancel();
        active?.Dispose();
    }
}
