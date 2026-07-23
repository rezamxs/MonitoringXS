using Microsoft.Extensions.Logging.Abstractions;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class ApplicationSortPresentationTests
{
    [Fact]
    public void ApplicationNameDefaultsToAscending()
    {
        Assert.False(ApplicationSortPresentation.DefaultDescending(
            ApplicationSortField.ApplicationName));
        Assert.Equal(
            "A to Z",
            ApplicationSortPresentation.DirectionLabel(
                ApplicationSortField.ApplicationName,
                descending: false));
        Assert.Equal(
            "Z to A",
            ApplicationSortPresentation.DirectionLabel(
                ApplicationSortField.ApplicationName,
                descending: true));
    }

    [Theory]
    [InlineData(ApplicationSortField.CpuUsage)]
    [InlineData(ApplicationSortField.MemoryUsage)]
    [InlineData(ApplicationSortField.ProcessIoRate)]
    [InlineData(ApplicationSortField.PhysicalDiskRate)]
    [InlineData(ApplicationSortField.NetworkRate)]
    [InlineData(ApplicationSortField.ProcessCount)]
    public void NumericMetricsDefaultToHighestFirst(ApplicationSortField field)
    {
        Assert.True(ApplicationSortPresentation.DefaultDescending(field));
        Assert.Equal(
            "Highest to lowest",
            ApplicationSortPresentation.DirectionLabel(field, descending: true));
        Assert.Equal(
            "Lowest to highest",
            ApplicationSortPresentation.DirectionLabel(field, descending: false));
    }

    [Fact]
    public void AutomationTextStatesCurrentAndNextDirection()
    {
        Assert.Equal(
            "Sort direction: Highest to lowest. Activate to change to Lowest to highest.",
            ApplicationSortPresentation.DirectionAutomationName(
                ApplicationSortField.CpuUsage,
                descending: true));
    }

    [Theory]
    [InlineData(ApplicationSortField.CpuUsage)]
    [InlineData(ApplicationSortField.MemoryUsage)]
    [InlineData(ApplicationSortField.ProcessIoRate)]
    [InlineData(ApplicationSortField.PhysicalDiskRate)]
    [InlineData(ApplicationSortField.NetworkRate)]
    [InlineData(ApplicationSortField.ProcessCount)]
    public void SelectingANumericFieldAppliesItsSmartDefault(ApplicationSortField field)
    {
        MainWindowViewModel viewModel = CreateViewModel();

        viewModel.SelectedSortOption = viewModel.SortOptions.Single(option => option.Field == field);

        Assert.True(viewModel.IsSortDescending);
        Assert.Equal("Highest to lowest", viewModel.SortDirectionLabel);
    }

    [Fact]
    public void SelectingNameAfterANumericFieldReturnsToAToZ()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.SelectedSortOption = viewModel.SortOptions.Single(
            option => option.Field == ApplicationSortField.CpuUsage);

        viewModel.SelectedSortOption = viewModel.SortOptions.Single(
            option => option.Field == ApplicationSortField.ApplicationName);

        Assert.False(viewModel.IsSortDescending);
        Assert.Equal("A to Z", viewModel.SortDirectionLabel);
    }

    [Fact]
    public void UserCanReverseTheSmartDefault()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.SelectedSortOption = viewModel.SortOptions.Single(
            option => option.Field == ApplicationSortField.NetworkRate);

        viewModel.ToggleSortDirection();

        Assert.False(viewModel.IsSortDescending);
        Assert.Equal("Lowest to highest", viewModel.SortDirectionLabel);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        EmptyMonitoringPipeline pipeline = new();
        MonitoringCoordinator coordinator = new(pipeline, pipeline, pipeline, pipeline);
        return new MainWindowViewModel(
            coordinator,
            NullLogger<MainWindowViewModel>.Instance);
    }

    private sealed class EmptyMonitoringPipeline :
        IProcessDiscoveryService,
        IApplicationAttributionService,
        IProcessMetricCollector,
        IMetricAggregationService
    {
        public ValueTask<IReadOnlyList<ProcessDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProcessDescriptor>>([]);

        public ValueTask<IReadOnlyList<AttributionResult>> AttributeAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AttributionResult>>([]);

        public ValueTask<IReadOnlyList<ProcessMetricSample>> CollectAsync(
            IReadOnlyList<ProcessDescriptor> processes,
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProcessMetricSample>>([]);

        public IReadOnlyList<ApplicationMetricSnapshot> Aggregate(
            IReadOnlyList<AttributionResult> attribution,
            IReadOnlyList<ProcessMetricSample> metrics,
            DateTimeOffset capturedAt) =>
            [];
    }
}
