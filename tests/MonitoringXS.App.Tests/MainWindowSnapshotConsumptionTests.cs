using Microsoft.Extensions.Logging.Abstractions;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class MainWindowSnapshotConsumptionTests
{
    [Fact]
    public void ApplyingSnapshotsPreservesSelectionSearchSortTabsAndLocalizationState()
    {
        LocalizationService localization = new();
        using MainWindowViewModel viewModel = new(
            NullLogger<MainWindowViewModel>.Instance,
            localization);
        viewModel.ApplySnapshot(Snapshot(10, 20));
        ApplicationCardViewModel selected = viewModel.InstalledApplications[0];
        viewModel.SelectedApplication = selected;
        viewModel.SearchText = selected.DisplayName;
        viewModel.SelectedSortOption = viewModel.SortOptions.Single(option =>
            option.Field == ApplicationSortField.CpuUsage);
        ApplicationTabViewModel tab = viewModel.OpenTab(selected);

        viewModel.ApplySnapshot(Snapshot(30, 5));
        localization.SetLanguage(ApplicationLanguage.Persian);

        Assert.Same(selected, viewModel.SelectedApplication);
        Assert.Equal(selected.DisplayName, viewModel.SearchText);
        Assert.Equal(ApplicationSortField.CpuUsage, viewModel.SelectedSortOption.Field);
        Assert.Same(tab, Assert.Single(viewModel.OpenTabs));
        Assert.Equal(2, viewModel.InstalledApplications.Count);
    }

    [Fact]
    public void HiddenRunningApplicationsSkipUpdatesAndCatchUpFromLatestWhenShown()
    {
        using MainWindowViewModel viewModel = new(
            NullLogger<MainWindowViewModel>.Instance,
            new LocalizationService());
        viewModel.ApplySnapshot(Snapshot(10, 20));
        ApplicationCardViewModel first = viewModel.InstalledApplications
            .Single(card => card.LogicalApplicationId == "one");

        viewModel.SetLiveSurfaces(LiveSurfaces.None);
        MonitoringSnapshot hidden = Snapshot(40, 50);
        viewModel.ApplySnapshot(hidden);
        viewModel.ApplySnapshot(Snapshot(70, 80));

        Assert.Equal(10, first.LatestSnapshot!.CpuPercent.Value);
        Assert.NotSame(hidden, viewModel.LatestDashboardSnapshot);

        viewModel.SetLiveSurfaces(LiveSurfaces.RunningApplications);

        Assert.Same(first, viewModel.InstalledApplications.Single(card => card.LogicalApplicationId == "one"));
        Assert.Equal(70, first.LatestSnapshot!.CpuPercent.Value);
        Assert.Equal(LiveSurfaces.RunningApplications, viewModel.LiveSurfaces);
    }

    [Fact]
    public void LiveRunningApplicationsKeepUpdatingWhileOverviewIsSkipped()
    {
        using MainWindowViewModel viewModel = new(
            NullLogger<MainWindowViewModel>.Instance,
            new LocalizationService());
        viewModel.SetLiveSurfaces(LiveSurfaces.RunningApplications);

        viewModel.ApplySnapshot(Snapshot(10, 20));
        viewModel.ApplySnapshot(Snapshot(30, 40));

        Assert.Equal(30, viewModel.InstalledApplications
            .Single(card => card.LogicalApplicationId == "one")
            .LatestSnapshot!.CpuPercent.Value);
    }

    [Fact]
    public void ShowingASurfaceWithNothingStaleDoesNotReapplyAnything()
    {
        using MainWindowViewModel viewModel = new(
            NullLogger<MainWindowViewModel>.Instance,
            new LocalizationService());

        viewModel.SetLiveSurfaces(LiveSurfaces.SystemOverview);

        Assert.Null(viewModel.LatestDashboardSnapshot);
        Assert.Empty(viewModel.InstalledApplications);
    }

    private static MonitoringSnapshot Snapshot(double firstCpu, double secondCpu)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        ApplicationMetricSnapshot first = Application("one", "One", 101, firstCpu, capturedAt);
        ApplicationMetricSnapshot second = Application("two", "Two", 102, secondCpu, capturedAt);
        return new(
            capturedAt,
            new ProcessDiscoverySnapshot([101, 102], [.. first.Processes, .. second.Processes], []),
            [first, second],
            new Dictionary<string, IReadOnlyList<ApplicationHistoryPoint>>(StringComparer.Ordinal)
            {
                ["one"] = [new(capturedAt, firstCpu, 1024)],
                ["two"] = [new(capturedAt, secondCpu, 1024)]
            });
    }

    private static ApplicationMetricSnapshot Application(
        string id,
        string name,
        int pid,
        double cpu,
        DateTimeOffset capturedAt)
    {
        ProcessDescriptor process = new(
            new ProcessInstanceId(pid, capturedAt.AddMinutes(-1)),
            name,
            null,
            name,
            null,
            null,
            name,
            null,
            false,
            true);
        return new(
            new ApplicationIdentity(
                id,
                name,
                null,
                ApplicationDisposition.Installed,
                null,
                ClassificationConfidence.High,
                "test"),
            capturedAt,
            MetricValue<double>.Available(cpu),
            MetricValue<long>.Available(1024),
            MetricValue<double>.Available(1),
            MetricValue<double>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            1,
            [process]);
    }
}
