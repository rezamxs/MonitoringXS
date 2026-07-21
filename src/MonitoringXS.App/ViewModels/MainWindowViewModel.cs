using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
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

    [ObservableProperty]
    public partial bool IsAdvancedMode { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Discovering running applications…";

    [ObservableProperty]
    public partial DateTimeOffset LastUpdated { get; set; }

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

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            MonitoringDashboardSnapshot snapshot = await Task.Run(
                async () => await _coordinator.CaptureAsync(cancellationToken),
                cancellationToken);

            UpdateCollection(InstalledApplications, snapshot.InstalledApplications, snapshot.OneMinuteHistory);
            UpdateCollection(PortableApplications, snapshot.PortableApplications, snapshot.OneMinuteHistory);
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

    private void UpdateCollection(
        ObservableCollection<ApplicationCardViewModel> target,
        IReadOnlyList<ApplicationMetricSnapshot> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<ApplicationHistoryPoint>> history)
    {
        HashSet<string> liveIds = snapshots.Select(item => item.Application.LogicalApplicationId).ToHashSet(StringComparer.Ordinal);
        foreach (ApplicationCardViewModel stale in target.Where(item => !liveIds.Contains(item.LogicalApplicationId)).ToArray())
        {
            target.Remove(stale);
            ApplicationItems.Remove(stale);
            _cards.Remove(stale.LogicalApplicationId);
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
                if (ReferenceEquals(target, InstalledApplications))
                {
                    int portableSectionIndex = ApplicationItems.IndexOf(_portableSection);
                    ApplicationItems.Insert(portableSectionIndex, card);
                }
                else
                {
                    ApplicationItems.Add(card);
                }
            }

            history.TryGetValue(card.LogicalApplicationId, out IReadOnlyList<ApplicationHistoryPoint>? points);
            card.Update(snapshot, points ?? []);
        }
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
