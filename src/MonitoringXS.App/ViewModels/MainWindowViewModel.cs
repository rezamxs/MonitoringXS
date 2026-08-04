using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MonitoringXS.Application;
using MonitoringXS.App.Localization;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
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
        new(ApplicationSortField.GpuUsage, "GPU usage"),
        new(ApplicationSortField.ProcessCount, "Process count")
    ];

    private readonly MonitoringCoordinator _coordinator;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IProcessActionService? _processActions;
    private readonly IClipboardService? _clipboard;
    private readonly Dictionary<string, ApplicationCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApplicationTabViewModel> _openTabs = new(StringComparer.Ordinal);
    private readonly LocalizationService _localization;
    private ApplicationSectionViewModel _installedSection = null!;
    private ApplicationSectionViewModel _portableSection = null!;
    private DateTimeOffset _lastLiveSortAt = DateTimeOffset.MinValue;
    private Func<ProcessActionConfirmation, CancellationToken, Task<bool>>? _confirmProcessAction;
    private CancellationToken _shutdownToken;

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

    public MainWindowViewModel(
        MonitoringCoordinator coordinator,
        ILogger<MainWindowViewModel> logger,
        LocalizationService? localization = null)
        : this(coordinator, logger, null, null, localization)
    {
    }

    public MainWindowViewModel(
        MonitoringCoordinator coordinator,
        ILogger<MainWindowViewModel> logger,
        IProcessActionService? processActions,
        IClipboardService? clipboard,
        LocalizationService? localization = null)
    {
        _coordinator = coordinator;
        _logger = logger;
        _processActions = processActions;
        _clipboard = clipboard;
        _localization = localization ?? new LocalizationService();
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
            if (sortField is ApplicationSortField.ApplicationName or ApplicationSortField.ProcessCount)
            {
                return false;
            }

            IEnumerable<ApplicationCardViewModel> cards = InstalledApplications.Concat(PortableApplications);
            return (InstalledApplications.Count > 0 || PortableApplications.Count > 0)
                && !ApplicationCardSorter.HasComparableData(cards, sortField);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            MonitoringDashboardSnapshot snapshot = await Task.Run(
                async () => await _coordinator.CaptureAsync(cancellationToken),
                cancellationToken);

            bool membershipChanged = UpdateCollection(
                InstalledApplications,
                snapshot.InstalledApplications,
                snapshot.OneMinuteHistory);
            membershipChanged |= UpdateCollection(
                PortableApplications,
                snapshot.PortableApplications,
                snapshot.OneMinuteHistory);
            ApplyCurrentSort(snapshot.CapturedAt, force: membershipChanged);
            OnPropertyChanged(nameof(HasNoComparableData));
            UpdateOpenTabs(snapshot);
            LastUpdated = snapshot.CapturedAt.ToLocalTime();
            UpdateStatusMessage();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportRefreshFailure(exception);
        }
    }

    internal void ReportRefreshFailure(Exception exception)
    {
        LogMonitoringRefreshFailed(_logger, exception);
        StatusMessage = _localization.Get(LocalizationKeys.RefreshRetry);
    }

    public ApplicationTabViewModel OpenTab(ApplicationCardViewModel card)
    {
        if (!_openTabs.TryGetValue(card.LogicalApplicationId, out ApplicationTabViewModel? tab))
        {
            ProcessActionsViewModel? actions =
                _processActions is null || _clipboard is null
                    ? null
                    : new ProcessActionsViewModel(_processActions, _clipboard, _localization);
            if (actions is not null && _confirmProcessAction is not null)
            {
                actions.Configure(
                    _confirmProcessAction,
                    RefreshAsync,
                    _shutdownToken);
            }

            tab = new ApplicationTabViewModel(
                card.LogicalApplicationId,
                card.DisplayName,
                actions,
                _localization);
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
        CancellationToken shutdownToken)
    {
        _confirmProcessAction = confirm;
        _shutdownToken = shutdownToken;
    }

    public void CloseTab(string logicalApplicationId) => _openTabs.Remove(logicalApplicationId);

    public void ToggleSortDirection() => IsSortDescending = !IsSortDescending;

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
                card = new ApplicationCardViewModel(_localization)
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
                    IsSortDescending));
            ApplyOrder(
                PortableApplications,
                ApplicationCardSorter.Sort(
                    PortableApplications,
                    SelectedSortOption.Field,
                    IsSortDescending));
            _lastLiveSortAt = capturedAt;
        }

        List<IApplicationListItemViewModel> desiredItems =
        [
            _installedSection,
            .. InstalledApplications,
            _portableSection,
            .. PortableApplications
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
        bool defaultDescending = ApplicationSortPresentation.DefaultDescending(value.Field);
        if (IsSortDescending != defaultDescending)
        {
            IsSortDescending = defaultDescending;
        }
        else
        {
            ApplyCurrentSort(DateTimeOffset.UtcNow, force: true);
        }

        NotifySortPresentationChanged();
    }

    partial void OnIsSortDescendingChanged(bool value)
    {
        NotifySortPresentationChanged();
        ApplyCurrentSort(DateTimeOffset.UtcNow, force: true);
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
                _ => _localization.Get(LocalizationKeys.SortProcessCount)
            }
        }).ToArray();
        ApplicationSortField selectedField = SelectedSortOption.Field;
        SelectedSortOption = SortOptions.Single(option => option.Field == selectedField);
        if (ApplicationItems.Count > 0)
        {
            ApplicationItems.Clear();
            ApplicationItems.Add(_installedSection);
            foreach (ApplicationCardViewModel card in InstalledApplications)
            {
                ApplicationItems.Add(card);
            }
            ApplicationItems.Add(_portableSection);
            foreach (ApplicationCardViewModel card in PortableApplications)
            {
                ApplicationItems.Add(card);
            }
        }
        OnPropertyChanged(nameof(SortOptions));
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs args)
    {
        BuildLocalizedPresentation();
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

    private void UpdateStatusMessage() => StatusMessage = _localization.Format(
        LocalizationKeys.DashboardStatus,
        InstalledApplications.Count,
        PortableApplications.Count,
        LastUpdated.ToString("T", _localization.Culture));

    private void UpdateOpenTabs(MonitoringDashboardSnapshot dashboard)
    {
        Dictionary<string, ApplicationMetricSnapshot> snapshots = dashboard.InstalledApplications
            .Concat(dashboard.PortableApplications)
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Monitoring refresh failed.")]
    private static partial void LogMonitoringRefreshFailed(ILogger logger, Exception exception);
}
