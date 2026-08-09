using System.Globalization;
using System.Xml.Linq;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class SystemOverviewPresentationTests
{
    [Fact]
    public void AvailableSnapshotFormatsEveryMachineWideMetric()
    {
        SystemOverviewPageViewModel viewModel = CreateViewModel();

        viewModel.Update(Snapshot(), []);

        Assert.Equal("42.5%", viewModel.PrimaryCards[0].PrimaryValue);
        Assert.Equal("8.0 GB / 16.0 GB", viewModel.PrimaryCards[1].PrimaryValue);
        Assert.Equal("8.0 GB", viewModel.PrimaryCards[1].SecondaryValue);
        Assert.Equal("1.5 MB/s", viewModel.SecondaryCards[0].PrimaryValue);
        Assert.Equal("2.0 MB/s", viewModel.SecondaryCards[0].SecondaryValue);
        Assert.Equal("3.0 KB/s", viewModel.SecondaryCards[1].PrimaryValue);
        Assert.Equal("4.0 KB/s", viewModel.SecondaryCards[1].SecondaryValue);
        Assert.Equal("25.0%", viewModel.SecondaryCards[2].PrimaryValue);
        Assert.All(viewModel.PrimaryCards.Concat(viewModel.SecondaryCards), card => Assert.False(card.HasStatus));
    }

    [Fact]
    public void SummaryAndDetailAreasReuseTheFiveStableMetricCards()
    {
        SystemOverviewPageViewModel viewModel = CreateViewModel();

        Assert.Equal(5, viewModel.SummaryCards.Count);
        Assert.Equal(
            ["CPU", "Physical Memory", "Physical Disk", "Network", "GPU"],
            viewModel.SummaryCards.Select(card => card.Title));
        Assert.Same(viewModel.PrimaryCards[0], viewModel.SummaryCards[0]);
        Assert.Same(viewModel.SecondaryCards[0], viewModel.SummaryCards[2]);
    }

    [Theory]
    [InlineData(MetricAvailability.WarmingUp, "Waiting")]
    [InlineData(MetricAvailability.Unavailable, "unavailable")]
    [InlineData(MetricAvailability.Unsupported, "not supported")]
    [InlineData(MetricAvailability.Error, "safely")]
    public void NonAvailableCpuNeverAppearsAsFakeZero(
        MetricAvailability availability,
        string expectedStatus)
    {
        SystemOverviewPageViewModel viewModel = CreateViewModel();
        SystemOverviewSnapshot snapshot = Snapshot() with
        {
            TotalCpuPercent = MetricValue<double>.Unavailable(availability),
            Diagnostics = Snapshot().Diagnostics with { CpuAvailability = availability }
        };

        viewModel.Update(snapshot, []);

        SystemOverviewMetricCardViewModel cpu = viewModel.PrimaryCards[0];
        Assert.DoesNotContain("0%", cpu.PrimaryValue, StringComparison.Ordinal);
        Assert.True(cpu.HasStatus);
        Assert.False(string.IsNullOrWhiteSpace(cpu.StatusLabel));
        Assert.Contains(expectedStatus, cpu.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialAndGpuUnsupportedArePresentedCalmly()
    {
        SystemOverviewPageViewModel viewModel = CreateViewModel();
        SystemOverviewSnapshot snapshot = Snapshot() with
        {
            DiskReadBytesPerSecond = MetricValue<double>.Partial(1024, "lower bound"),
            GpuUtilizationPercent = MetricValue<double>.Unavailable(MetricAvailability.Unsupported),
            Diagnostics = Snapshot().Diagnostics with
            {
                DiskAvailability = MetricAvailability.Partial,
                GpuAvailability = MetricAvailability.Unsupported
            }
        };

        viewModel.Update(snapshot, []);

        // Partial status hides the visible badge but keeps the ≥ prefix and tooltip explanation.
        Assert.StartsWith("≥ ", viewModel.SecondaryCards[0].PrimaryValue, StringComparison.Ordinal);
        Assert.False(viewModel.SecondaryCards[0].HasStatus);
        Assert.Equal(string.Empty, viewModel.SecondaryCards[0].StatusLabel);
        Assert.Contains("incomplete", viewModel.SecondaryCards[0].StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Unsupported", viewModel.SecondaryCards[2].PrimaryValue);
        Assert.Contains("not supported", viewModel.SecondaryCards[2].StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoryIsBoundedPreservesGapsAndCollectionIdentity()
    {
        SystemOverviewPageViewModel viewModel = CreateViewModel();
        var cpuSamples = viewModel.PrimaryCards[0].PrimarySamples;
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SystemOverviewHistoryPoint[] history = Enumerable.Range(0, 61)
            .Select(index => new SystemOverviewHistoryPoint(
                start.AddSeconds(index),
                index == 30 ? null : index,
                50,
                index,
                index,
                index,
                index,
                20))
            .ToArray();

        viewModel.Update(Snapshot(), history);
        viewModel.Update(Snapshot(), history);

        Assert.Same(cpuSamples, viewModel.PrimaryCards[0].PrimarySamples);
        Assert.Equal(60, cpuSamples.Count);
        Assert.Null(cpuSamples[29].Value);
        Assert.Equal(1, cpuSamples[0].Value);
    }

    [Fact]
    public void ImpossiblePercentBecomesErrorAndChartGap()
    {
        SystemOverviewPageViewModel viewModel = CreateViewModel();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SystemOverviewSnapshot snapshot = Snapshot() with
        {
            TotalCpuPercent = MetricValue<double>.Available(150)
        };

        viewModel.Update(snapshot, [new(now, 150, 50, 0, 0, 0, 0, 0)]);

        Assert.Equal("Error", viewModel.PrimaryCards[0].PrimaryValue);
        Assert.Null(viewModel.PrimaryCards[0].PrimarySamples[0].Value);
    }

    [Fact]
    public void PersianPresentationRelocalizesWithoutReplacingCharts()
    {
        LocalizationService localization = CreateLocalization();
        SystemOverviewPageViewModel viewModel = new(localization);
        var samples = viewModel.PrimaryCards[0].PrimarySamples;

        localization.SetLanguage(ApplicationLanguage.Persian);
        viewModel.Relocalize();

        Assert.Equal("نمای کلی سیستم", viewModel.PageTitle);
        Assert.Same(samples, viewModel.PrimaryCards[0].PrimarySamples);
        Assert.Equal(TextDirection.RightToLeft, localization.Direction);
    }

    [Fact]
    public void NavigationAndTechnicalValuesDeclareExpectedDirection()
    {
        string root = FindRepositoryRoot();
        string main = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "MainWindow.xaml"));
        string page = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "SystemOverviewPage.xaml"));

        Assert.Contains("Tag=\"system-overview\"", main, StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{x:Bind ViewModel.SystemOverview, Mode=OneTime}\"", main, StringComparison.Ordinal);
        Assert.Contains("FlowDirection=\"LeftToRight\"", page, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", page, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding SummaryCards}\"", page, StringComparison.Ordinal);
        Assert.Contains("MaximumRowsOrColumns=\"5\"", page, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ActivityMetricTemplate\"", page, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"760\"", page, StringComparison.Ordinal);
        Assert.Contains("ShowSummary=\"False\"", page, StringComparison.Ordinal);
        Assert.Contains("IsEmbedded=\"True\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactPartialStatusHasEnglishAndPersianParity()
    {
        string root = FindRepositoryRoot();
        string[] cultures = ["en-US", "fa-IR"];
        string[] values = cultures
            .Select(culture => XDocument.Load(Path.Combine(
                root,
                "src",
                "MonitoringXS.App",
                "Strings",
                culture,
                "Resources.resw")))
            .Select(document => document.Root!
                .Elements("data")
                .Single(element => (string?)element.Attribute("name") == "SystemOverviewStatusPartialLabel")
                .Element("value")!.Value)
            .ToArray();

        Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal("Partial", values[0]);
    }

    private static SystemOverviewPageViewModel CreateViewModel() => new(CreateLocalization());

    private static LocalizationService CreateLocalization() => new(
        Path.Combine(FindRepositoryRoot(), "src", "MonitoringXS.App"),
        CultureInfo.GetCultureInfo("en-US"));

    private static SystemOverviewSnapshot Snapshot()
    {
        SystemOverviewDiagnostics diagnostics = new(
            MetricAvailability.Available,
            null,
            MetricAvailability.Available,
            null,
            MetricAvailability.Available,
            null,
            MetricAvailability.Available,
            null,
            MetricAvailability.Available,
            null,
            1,
            true);
        return new(
            DateTimeOffset.UtcNow,
            MetricValue<double>.Available(42.5),
            MetricValue<long>.Available(16L * 1024 * 1024 * 1024),
            MetricValue<long>.Available(8L * 1024 * 1024 * 1024),
            MetricValue<long>.Available(8L * 1024 * 1024 * 1024),
            MetricValue<double>.Available(50),
            MetricValue<double>.Available(1.5 * 1024 * 1024),
            MetricValue<double>.Available(2 * 1024 * 1024),
            MetricValue<double>.Available(3 * 1024),
            MetricValue<double>.Available(4 * 1024),
            MetricValue<double>.Available(25),
            diagnostics);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MonitoringXS.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
