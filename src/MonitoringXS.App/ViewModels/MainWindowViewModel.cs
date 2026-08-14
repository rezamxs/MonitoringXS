using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MonitoringXS.Application;
using MonitoringXS.App.Localization;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan LiveSortInterval = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlyList<ApplicationSortOption> AvailableSortOptions =
    [
        new(ApplicationSortField.ApplicationName, "Application name"),
        new(ApplicationSortField.CpuUsage, "CPU usage"),
        new(ApplicationSortField.MemoryUsage, "Memory usage"),
        new(ApplicationSortField.ProcessIoRate, "Process I/O rate"),
        new(ApplicationSortField.PhysicalDiskRate, "Physical Disk rate"),
        new(ApplicationSortField.NetworkRate, "Network rate"),
        new(ApplicationSortField.GpuUsage, "GPU usage")
    ];

    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IProcessActionService? _processActions;
    private readonly IClipboardService? _clipboard;
    private readonly Dictionary<string, ApplicationCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApplicationTabViewModel> _openTabs = new(StringComparer.Ordinal);
    private readonly LocalizationService _localization;
    private readonly MetricExplanationService _metricExplanations;
    private readonly IApplicationIconProvider? _iconProvider;
    private ApplicationSectionViewModel _installedSection = null!;
    private ApplicationSectionViewModel _portableSection = null!;
    private DateTimeOffset _lastLiveSortAt = DateTimeOffset.MinValue;
    private Func<ProcessActionConfirmation, CancellationToken, Task<bool>>? _confirmProcessAction;
    private Func<CancellationToken, Task>? _refreshAfterProcessAction;
    private Func<ApplicationSortPreference, bool, CancellationToken, Task>? _persistSort;
    private CancellationToken _shutdownToken;
    private bool _changingSortField;
    private bool _suppressSortPersistence;

    [ObservableProperty]
    public partial bool IsAdvancedMode { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset LastUpdated { get; set; }

    [ObservableProperty]
    public partial ApplicationSortOption SelectedSortOption { get; set; } = AvailableSortOptions[0];

    [ObservableProperty]
    public partial bool IsSortDescending { get; set; }

    [ObservableProperty]
    public partial ApplicationCardViewModel? SelectedApplication { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        LocalizationService? localization = null,
        MetricExplanationService? metricExplanations = null)
        : this(logger, null, null, localization, metricExplanations, null)
    {
    }

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        IProcessActionService? processActions,
        IClipboardService? clipboard,
        LocalizationService? localization = null,
        MetricExplanationService? metricExplanations = null,
        IApplicationIconProvider? iconProvider = null)
    {
        _logger = logger;
        _processActions = processActions;
        _clipboard = clipboard;
        _localization = localization ?? new LocalizationService();
        _metricExplanations = metricExplanations ?? new MetricExplanationService(_localization);
        _iconProvider = iconProvider;
        SystemOverview = new SystemOverviewPageViewModel(_localization);
        BuildLocalizedPresentation();
        StatusMessage = _localization.Get(LocalizationKeys.DiscoveringApplications);
        _localization.LanguageChanged += Localization_LanguageChanged;
        ApplicationItems.Add(_installedSection);
        ApplicationItems.Add(_portableSection);
    }

    public ObservableCollection<ApplicationCardViewModel> InstalledApplications { get; } = [];

    public ObservableCollection<ApplicationCardViewModel> PortableApplications { get; } = [];

    public ObservableCollection<IApplicationListItemViewModel> ApplicationItems { get; } = [];

    public IReadOnlyList<ApplicationSortOption> SortOptions { get; private set; } = [];

    public IReadOnlyCollection<ApplicationTabViewModel> OpenTabs => _openTabs.Values;

    public MonitoringSnapshot? LatestDashboardSnapshot { get; private set; }

    public SystemOverviewPageViewModel SystemOverview { get; }

    public string SortDirectionLabel => ApplicationSortPresentation.DirectionLabel(
        SelectedSortOption.Field,
        IsSortDescending,
        _localization);

    public string SortDirectionAutomationName => ApplicationSortPresentation.DirectionAutomationName(
        SelectedSortOption.Field,
        IsSortDescending,
        _localization);

    public bool HasNoComparableData
    {
        get
        {
            ApplicationSortField sortField = SelectedSortOption.Field;
            if (sortField == ApplicationSortField.ApplicationName)
            {
                return false;
            }

            ApplicationCardViewModel[] cards = VisibleCards().ToArray();
            return cards.Length > 0
                && !ApplicationCardSorter.HasComparableData(cards, sortField);
        }
    }

    public bool HasNoSearchResults =>
        !string.IsNullOrWhiteSpace(SearchText) && !VisibleCards().Any();

    public void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LatestDashboardSnapshot = snapshot;
        SystemOverview.Update(snapshot.SystemOverview, snapshot.SystemOverviewHistory ?? []);
        ApplicationMetricSnapshot[] installed = snapshot.Applications
            .Where(item => item.Application.Disposition is ApplicationDisposition.Installed or ApplicationDisposition.Packaged)
            .ToArray();
        ApplicationMetricSnapshot[] portable = snapshot.Applications
            .Where(item => item.Application.Disposition is ApplicationDisposition.Portable or ApplicationDisposition.Unresolved)
            .ToArray();

        bool membershipChanged = UpdateCollection(
            InstalledApplications,
            installed,
            snapshot.OneMinuteHistory);
        membershipChanged |= UpdateCollection(
            PortableApplications,
            portable,
            snapshot.OneMinuteHistory);
        ApplyCurrentSort(snapshot.CapturedAt, force: membershipChanged);
        OnPropertyChanged(nameof(HasNoComparableData));
        OnPropertyChanged(nameof(HasNoSearchResults));
        UpdateOpenTabs(snapshot);
        LastUpdated = snapshot.CapturedAt.ToLocalTime();
        UpdateStatusMessage();
    }

    public ApplicationTabViewModel OpenTab(ApplicationCardViewModel card)
    {
        if (!_openTabs.TryGetValue(card.LogicalApplicationId, out ApplicationTabViewModel? tab))
        {
            ProcessActionsViewModel? actions =
                _processActions is null || _clipboard is null
                    ? null
                    : new ProcessActionsViewModel(_processActions, _clipboard, _localization);
            if (actions is not null
                && _confirmProcessAction is not null
                && _refreshAfterProcessAction is not null)
            {
                actions.Configure(
                    _confirmProcessAction,
                    _refreshAfterProcessAction,
                    _shutdownToken);
            }

            tab = new ApplicationTabViewModel(
                card.LogicalApplicationId,
                card.DisplayName,
                actions,
                _localization,
                _metricExplanations,
                _iconProvider);
            _openTabs.Add(card.LogicalApplicationId, tab);
        }

        if (card.LatestSnapshot is not null)
        {
            tab.Update(card.LatestSnapshot, card.History);
        }

        return tab;
    }

    internal void ConfigureProcessActions(
        Func<ProcessActionConfirmation, CancellationToken, Task<bool>> confirm,
        Func<CancellationToken, Task> refresh,
        CancellationToken shutdownToken)
    {
        _confirmProcessAction = confirm;
        _refreshAfterProcessAction = refresh;
        _shutdownToken = shutdownToken;
    }

    public void CloseTab(string logicalApplicationId)
    {
        if (_openTabs.Remove(logicalApplicationId, out ApplicationTabViewModel? tab))
        {
            tab.Dispose();
        }
    }

    public void ToggleSortDirection() => IsSortDescending = !IsSortDescending;

    public void ClearSearch() => SearchText = string.Empty;

    internal void ConfigureApplicationSort(
        ApplicationSettings settings,
        Func<ApplicationSortPreference, bool, CancellationToken, Task> persistSort,
        CancellationToken shutdownToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(persistSort);
        _persistSort = persistSort;
        _shutdownToken = shutdownToken;
        _suppressSortPersistence = true;
        try
        {
            ApplicationSortField field = FromPreference(settings.ApplicationSort);
            SelectedSortOption = SortOptions.Single(option => option.Field == field);
            IsSortDescending = settings.ApplicationSortDescending;
            ApplyCurrentSort(DateTimeOffset.UtcNow, force: true);
        }
        finally
        {
            _suppressSortPersistence = false;
        }
    }

    private bool UpdateCollection(
        ObservableCollection<ApplicationCardViewModel> target,
        IReadOnlyList<ApplicationMetricSnapshot> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<ApplicationHistoryPoint>> history)
    {
        bool membershipChanged = false;
        HashSet<string> liveIds = snapshots.Select(item => item.Application.LogicalApplicationId).ToHashSet(StringComparer.Ordinal);
        foreach (ApplicationCardViewModel stale in target.Where(item => !liveIds.Contains(item.LogicalApplicationId)).ToArray())
        {
            if (ReferenceEquals(SelectedApplication, stale))
            {
                SelectedApplication = null;
            }

            target.Remove(stale);
            _cards.Remove(stale.LogicalApplicationId);
            membershipChanged = true;
        }

        foreach (ApplicationMetricSnapshot snapshot in snapshots)
        {
            if (!_cards.TryGetValue(snapshot.Application.LogicalApplicationId, out ApplicationCardViewModel? card))
            {
                card = new ApplicationCardViewModel(_localization, _metricExplanations, _iconProvider)
                {
                    LogicalApplicationId = snapshot.Application.LogicalApplicationId,
                    Disposition = snapshot.Application.Disposition
                };
                _cards.Add(card.LogicalApplicationId, card);
                target.Add(card);
                membershipChanged = true;
            }

            history.TryGetValue(card.LogicalApplicationId, out IReadOnlyList<ApplicationHistoryPoint>? points);
            card.Update(snapshot, points ?? []);
        }

        return membershipChanged;
    }

    private void ApplyCurrentSort(DateTimeOffset capturedAt, bool force)
    {
        ApplicationCardViewModel? selectedApplication = SelectedApplication;
        bool sortDue = ApplicationSortRefreshPolicy.IsRefreshDue(
            _lastLiveSortAt,
            capturedAt,
            LiveSortInterval,
            force);
        if (sortDue)
        {
            ApplyOrder(
                InstalledApplications,
                ApplicationCardSorter.Sort(
                    InstalledApplications,
                    SelectedSortOption.Field,
                    IsSortDescending,
                    _localization.Culture));
            ApplyOrder(
                PortableApplications,
                ApplicationCardSorter.Sort(
                    PortableApplications,
                    SelectedSortOption.Field,
                    IsSortDescending,
                    _localization.Culture));
            _lastLiveSortAt = capturedAt;
        }

        List<IApplicationListItemViewModel> desiredItems =
        [
            _installedSection,
            .. InstalledApplications.Where(MatchesSearch),
            _portableSection,
            .. PortableApplications.Where(MatchesSearch)
        ];
        ApplyOrder(ApplicationItems, desiredItems);

        if (sortDue && selectedApplication is not null && _cards.ContainsKey(selectedApplication.LogicalApplicationId))
        {
            SelectedApplication = selectedApplication;
            OnPropertyChanged(nameof(SelectedApplication));
        }
    }

    private static void ApplyOrder<T>(ObservableCollection<T> collection, IReadOnlyList<T> desiredOrder)
        where T : class
    {
        for (int desiredIndex = 0; desiredIndex < desiredOrder.Count; desiredIndex++)
        {
            T desiredItem = desiredOrder[desiredIndex];
            if (desiredIndex < collection.Count && ReferenceEquals(collection[desiredIndex], desiredItem))
            {
                continue;
            }

            int currentIndex = collection.IndexOf(desiredItem);
            if (currentIndex >= 0)
            {
                collection.Move(currentIndex, desiredIndex);
            }
            else
            {
                collection.Insert(desiredIndex, desiredItem);
            }
        }

        while (collection.Count > desiredOrder.Count)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

    partial void OnSelectedSortOptionChanged(ApplicationSortOption value)
    {
        _changingSortField = true;
        try
        {
            bool defaultDescending = ApplicationSortPresentation.DefaultDescending(value.Field);
            if (IsSortDescending != defaultDescending)
            {
                IsSortDescending = defaultDescending;
            }
            else
            {
                ApplyCurrentSort(DateTimeOffset.UtcNow, force: true);
            }
        }
        finally
        {
            _changingSortField = false;
        }

        NotifySortPresentationChanged();
        PersistSortPreference();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        NotifySortPresentationChanged();
        ApplyCurrentSort(DateTimeOffset.UtcNow, force: true);
        if (!_changingSortField)
        {
            PersistSortPreference();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyCurrentSort(DateTimeOffset.UtcNow, force: true);
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(HasNoComparableData));
    }

    private void NotifySortPresentationChanged()
    {
        OnPropertyChanged(nameof(SortDirectionLabel));
        OnPropertyChanged(nameof(SortDirectionAutomationName));
        OnPropertyChanged(nameof(HasNoComparableData));
    }

    private void BuildLocalizedPresentation()
    {
        _installedSection = new(
            _localization.Get(LocalizationKeys.InstalledApplications),
            _localization.Get(LocalizationKeys.InstalledApplicationsDescription));
        _portableSection = new(
            _localization.Get(LocalizationKeys.PortableApplications),
            _localization.Get(LocalizationKeys.PortableApplicationsDescription));
        SortOptions = AvailableSortOptions.Select(option => option with
        {
            Label = option.Field switch
            {
                ApplicationSortField.ApplicationName => _localization.Get(LocalizationKeys.SortApplicationName),
                ApplicationSortField.CpuUsage => _localization.Get(LocalizationKeys.SortCpu),
                ApplicationSortField.MemoryUsage => _localization.Get(LocalizationKeys.SortMemory),
                ApplicationSortField.ProcessIoRate => _localization.Get(LocalizationKeys.SortProcessIo),
                ApplicationSortField.PhysicalDiskRate => _localization.Get(LocalizationKeys.SortDisk),
                ApplicationSortField.NetworkRate => _localization.Get(LocalizationKeys.SortNetwork),
                ApplicationSortField.GpuUsage => _localization.Get(LocalizationKeys.SortGpu),
                _ => throw new ArgumentOutOfRangeException(nameof(option.Field))
            }
        }).ToArray();
        ApplicationSortField selectedField = SelectedSortOption.Field;
        SelectedSortOption = SortOptions.Single(option => option.Field == selectedField);
        if (ApplicationItems.Count > 0)
        {
            ApplicationItems.Clear();
            ApplicationItems.Add(_installedSection);
            foreach (ApplicationCardViewModel card in InstalledApplications.Where(MatchesSearch))
            {
                ApplicationItems.Add(card);
            }
            ApplicationItems.Add(_portableSection);
            foreach (ApplicationCardViewModel card in PortableApplications.Where(MatchesSearch))
            {
                ApplicationItems.Add(card);
            }
        }
        OnPropertyChanged(nameof(SortOptions));
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs args)
    {
        BuildLocalizedPresentation();
        SystemOverview.Relocalize();
        foreach (ApplicationCardViewModel card in _cards.Values)
        {
            card.Relocalize();
        }

        foreach (ApplicationTabViewModel tab in _openTabs.Values)
        {
            tab.Relocalize();
        }

        NotifySortPresentationChanged();
        UpdateStatusMessage();
    }

    private IEnumerable<ApplicationCardViewModel> VisibleCards() =>
        InstalledApplications.Concat(PortableApplications).Where(MatchesSearch);

    private bool MatchesSearch(ApplicationCardViewModel card) =>
        ApplicationSearchMatcher.Matches(card, SearchText, _localization.Culture);

    private void PersistSortPreference()
    {
        if (_suppressSortPersistence || _persistSort is null)
        {
            return;
        }

        _ = PersistSortPreferenceAsync(
            ToPreference(SelectedSortOption.Field),
            IsSortDescending,
            _shutdownToken);
    }

    private async Task PersistSortPreferenceAsync(
        ApplicationSortPreference field,
        bool descending,
        CancellationToken cancellationToken)
    {
        try
        {
            await _persistSort!(field, descending, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogSortPreferenceSaveFailed(_logger, exception);
        }
    }

    private static ApplicationSortPreference ToPreference(ApplicationSortField field) => field switch
    {
        ApplicationSortField.ApplicationName => ApplicationSortPreference.Name,
        ApplicationSortField.CpuUsage => ApplicationSortPreference.Cpu,
        ApplicationSortField.MemoryUsage => ApplicationSortPreference.Memory,
        ApplicationSortField.ProcessIoRate => ApplicationSortPreference.ProcessIo,
        ApplicationSortField.PhysicalDiskRate => ApplicationSortPreference.PhysicalDisk,
        ApplicationSortField.NetworkRate => ApplicationSortPreference.Network,
        ApplicationSortField.GpuUsage => ApplicationSortPreference.Gpu,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static ApplicationSortField FromPreference(ApplicationSortPreference preference) => preference switch
    {
        ApplicationSortPreference.Name => ApplicationSortField.ApplicationName,
        ApplicationSortPreference.Cpu => ApplicationSortField.CpuUsage,
        ApplicationSortPreference.Memory => ApplicationSortField.MemoryUsage,
        ApplicationSortPreference.ProcessIo => ApplicationSortField.ProcessIoRate,
        ApplicationSortPreference.PhysicalDisk => ApplicationSortField.PhysicalDiskRate,
        ApplicationSortPreference.Network => ApplicationSortField.NetworkRate,
        ApplicationSortPreference.Gpu => ApplicationSortField.GpuUsage,
        _ => ApplicationSortField.ApplicationName
    };

    private void UpdateStatusMessage() => StatusMessage = _localization.Format(
        LocalizationKeys.DashboardStatus,
        InstalledApplications.Count,
        PortableApplications.Count,
        LastUpdated.ToString("T", _localization.Culture));

    public void Dispose()
    {
        _localization.LanguageChanged -= Localization_LanguageChanged;
        foreach (ApplicationTabViewModel tab in _openTabs.Values)
        {
            tab.Dispose();
        }

        _openTabs.Clear();
    }

    private void UpdateOpenTabs(MonitoringSnapshot dashboard)
    {
        Dictionary<string, ApplicationMetricSnapshot> snapshots = dashboard.Applications
            .ToDictionary(item => item.Application.LogicalApplicationId, StringComparer.Ordinal);

        foreach (ApplicationTabViewModel tab in _openTabs.Values)
        {
            if (snapshots.TryGetValue(tab.LogicalApplicationId, out ApplicationMetricSnapshot? snapshot))
            {
                dashboard.OneMinuteHistory.TryGetValue(tab.LogicalApplicationId, out IReadOnlyList<ApplicationHistoryPoint>? points);
                tab.Update(snapshot, points ?? []);
            }
        }
    }

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Application sort preference could not be saved.")]
    private static partial void LogSortPreferenceSaveFailed(ILogger logger, Exception exception);
}
