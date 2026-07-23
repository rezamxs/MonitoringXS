using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MonitoringXS.Application;
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
        new(ApplicationSortField.ProcessCount, "Process count")
    ];

    private readonly MonitoringCoordinator _coordinator;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dictionary<string, ApplicationCardViewModel> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApplicationTabViewModel> _openTabs = new(StringComparer.Ordinal);
    private readonly ApplicationSectionViewModel _installedSection = new(
        "Installed applications",
        "Select an application to open its live tab. Windows infrastructure and services are excluded.");
    private readonly ApplicationSectionViewModel _portableSection = new(
        "Portable & unregistered apps",
        "Executables without catalog-backed installation evidence remain separate.");
    private DateTimeOffset _lastLiveSortAt = DateTimeOffset.MinValue;

    [ObservableProperty]
    public partial bool IsAdvancedMode { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Discovering running applications…";

    [ObservableProperty]
    public partial DateTimeOffset LastUpdated { get; set; }

    [ObservableProperty]
    public partial ApplicationSortOption SelectedSortOption { get; set; } = AvailableSortOptions[0];

    [ObservableProperty]
    public partial bool IsSortDescending { get; set; }

    [ObservableProperty]
    public partial ApplicationCardViewModel? SelectedApplication { get; set; }

    public MainWindowViewModel(MonitoringCoordinator coordinator, ILogger<MainWindowViewModel> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
        ApplicationItems.Add(_installedSection);
        ApplicationItems.Add(_portableSection);
    }

    public ObservableCollection<ApplicationCardViewModel> InstalledApplications { get; } = [];

    public ObservableCollection<ApplicationCardViewModel> PortableApplications { get; } = [];

    public ObservableCollection<IApplicationListItemViewModel> ApplicationItems { get; } = [];

    public IReadOnlyList<ApplicationSortOption> SortOptions { get; } = AvailableSortOptions;

    public string SortDirectionLabel => ApplicationSortPresentation.DirectionLabel(
        SelectedSortOption.Field,
        IsSortDescending);

    public string SortDirectionAutomationName => ApplicationSortPresentation.DirectionAutomationName(
        SelectedSortOption.Field,
        IsSortDescending);

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
            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"{InstalledApplications.Count} installed · {PortableApplications.Count} portable · updated {LastUpdated:HH:mm:ss}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogMonitoringRefreshFailed(_logger, exception);
            StatusMessage = "Some monitoring data is temporarily unavailable. Retrying…";
        }
    }

    public ApplicationTabViewModel OpenTab(ApplicationCardViewModel card)
    {
        if (!_openTabs.TryGetValue(card.LogicalApplicationId, out ApplicationTabViewModel? tab))
        {
            tab = new ApplicationTabViewModel(card.LogicalApplicationId, card.DisplayName);
            _openTabs.Add(card.LogicalApplicationId, tab);
        }

        if (card.LatestSnapshot is not null)
        {
            tab.Update(card.LatestSnapshot, card.History);
        }

        return tab;
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
                card = new ApplicationCardViewModel
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
