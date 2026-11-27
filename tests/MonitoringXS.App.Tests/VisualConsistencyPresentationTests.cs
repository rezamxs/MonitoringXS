using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Controls;

namespace MonitoringXS.App.Tests;

public sealed class VisualConsistencyPresentationTests
{
    private static readonly string[] SymbolPages =
    [
        "MainWindow.xaml", "SystemOverviewPage.xaml", "HistoryPage.xaml",
        "DiagnosticsPage.xaml", "SettingsPage.xaml", "AboutPage.xaml"
    ];
    private static readonly string[] IconViewModels =
    [
        "ApplicationCardViewModel.cs", "ApplicationTabViewModel.cs"
    ];

    [Fact]
    public void PrimaryPagesReuseThePrecisionGlassHierarchy()
    {
        string root = FindRepositoryRoot();
        string app = Read(root, "App.xaml");
        string main = Read(root, "MainWindow.xaml");
        string history = Read(root, "HistoryPage.xaml");
        string diagnostics = Read(root, "DiagnosticsPage.xaml");
        string settings = Read(root, "SettingsPage.xaml");
        string about = Read(root, "AboutPage.xaml");

        Assert.Contains("x:Key=\"PageTitleStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SectionSurfaceStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("PaneDisplayMode=\"Auto\"", main, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"1800\"", main, StringComparison.Ordinal);
        Assert.Contains("ItemsStretch=\"Fill\"", history, StringComparison.Ordinal);
        Assert.Equal(1, Count(history, "x:Uid=\"HistoryChartLegend\""));
        Assert.Contains("MaxWidth=\"1600\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"940\"", settings, StringComparison.Ordinal);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"960\"", about, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsivePagesAvoidNormalHorizontalScrolling()
    {
        string root = FindRepositoryRoot();
        foreach (string page in new[] { "MainWindow.xaml", "HistoryPage.xaml", "DiagnosticsPage.xaml", "SettingsPage.xaml", "AboutPage.xaml" })
        {
            string xaml = Read(root, page);
            Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Visible\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NavigationSymbolsAreValidWinUiSymbols()
    {
        string root = FindRepositoryRoot();
        string main = Read(root, "MainWindow.xaml");
        MatchCollection icons = Regex.Matches(main, "\\bIcon=\"(?<icon>[^\"]+)\"");
        string allPages = string.Join('\n', SymbolPages.Select(page => Read(root, page)));
        MatchCollection symbols = Regex.Matches(allPages, "\\bSymbol=\"(?<icon>[^\"]+)\"");

        Assert.NotEmpty(icons);
        Assert.All(icons.Cast<Match>(), match =>
            Assert.True(Enum.TryParse(match.Groups["icon"].Value, out Symbol _), match.Value));
        Assert.NotEmpty(symbols);
        Assert.All(symbols.Cast<Match>(), match =>
            Assert.True(Enum.TryParse(match.Groups["icon"].Value, out Symbol _), match.Value));
        Assert.DoesNotContain("Icon=\"ViewDashboard\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningAppsTabsAndDetailsUseTheExistingIconSourceWithANativeFallback()
    {
        string main = Read(FindRepositoryRoot(), "MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement[] icons = XDocument.Parse(main)
            .Descendants(presentation + "Image")
            .Where(image => image.Attribute("Source")?.Value.Contains("AppIconSource", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Contains("x:Key=\"ApplicationTabHeaderTemplate\"", main, StringComparison.Ordinal);
        Assert.Equal(4, icons.Length);
        Assert.All(icons, image =>
        {
            Assert.Contains("HasAppIcon", image.Attribute("Visibility")?.Value ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal("Grid", image.Parent?.Name.LocalName);
            Assert.Null(image.Parent?.Attribute("Background"));
            XElement fallback = Assert.Single(image.Parent!.Elements(presentation + "FontIcon"));
            Assert.Equal("\uE71D", fallback.Attribute("Glyph")?.Value);
            Assert.Contains("HasFallbackIcon", fallback.Attribute("Visibility")?.Value ?? string.Empty, StringComparison.Ordinal);
            Assert.InRange(double.Parse(image.Attribute("Width")!.Value, CultureInfo.InvariantCulture), 20, 32);
            Assert.Equal("Uniform", image.Attribute("Stretch")?.Value);
        });
    }

    [Fact]
    public void MetricIconsAreSemanticAndSharedAcrossOverviewAndRunningApps()
    {
        string root = FindRepositoryRoot();
        string main = Read(root, "MainWindow.xaml");
        string overview = File.ReadAllText(Path.Combine(
            root, "src", "MonitoringXS.App", "ViewModels", "SystemOverviewPageViewModel.cs"));

        Assert.Equal("\uE950", MetricGlyphs.Cpu);
        Assert.Equal("\uE964", MetricGlyphs.Memory);
        Assert.Equal("\uEDA2", MetricGlyphs.Disk);
        Assert.Equal("\uE774", MetricGlyphs.Network);
        Assert.Equal("\uE7F4", MetricGlyphs.Gpu);
        Assert.Equal(5, new[] { MetricGlyphs.Cpu, MetricGlyphs.Memory, MetricGlyphs.Disk, MetricGlyphs.Network, MetricGlyphs.Gpu }.Distinct().Count());

        // Settings gears (E9F5), code braces (E943) and the PC tower (E968)
        // were used for CPU, GPU and Network; none of them may come back.
        foreach (string wrong in new[] { "E9F5", "E943", "E968" })
        {
            Assert.DoesNotContain($"&#x{wrong};", main, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"\\u{wrong}", overview, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("MetricGlyphs.Cpu", overview, StringComparison.Ordinal);
        Assert.Contains("MetricGlyphs.Gpu", overview, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE950;\"", main, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE7F4;\"", main, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE774;\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncIconLoadersKeepTheLateResultPathGuard()
    {
        string root = FindRepositoryRoot();
        foreach (string viewModel in IconViewModels)
        {
            string source = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "ViewModels", viewModel));
            Assert.Contains(
                "string.Equals(executablePath, _currentIconPath, StringComparison.OrdinalIgnoreCase)",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OverviewCardsUseEmbeddedChartsAndExplicitMetricLabels()
    {
        string root = FindRepositoryRoot();
        string overview = Read(root, "SystemOverviewPage.xaml");
        string chart = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "Controls", "MetricSparkline.xaml.cs"));

        // Summary, primary, and one chart per activity series (read/write or receive/send).
        Assert.Equal(4, Count(overview, "IsEmbedded=\"True\""));
        Assert.DoesNotContain("AdaptiveTrigger", overview, StringComparison.Ordinal);
        Assert.Contains("StretchDirection=\"DownOnly\"", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"120\"", overview, StringComparison.Ordinal);
        Assert.Contains("leftInset = isEmbedded ? 2 : LeftInset", File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "Controls", "CpuHistorySeries.cs")), StringComparison.Ordinal);
        Assert.Contains("TopAxisLabel.Visibility = Visibility.Collapsed", chart, StringComparison.Ordinal);
        Assert.Contains("StartAxisLabel.Visibility = Visibility.Collapsed", chart, StringComparison.Ordinal);

        string english = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "Strings", "en-US", "Resources.resw"));
        string persian = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "Strings", "fa-IR", "Resources.resw"));
        Assert.Contains("<value>Disk read</value>", english, StringComparison.Ordinal);
        Assert.Contains("<value>Network receive</value>", english, StringComparison.Ordinal);
        Assert.Contains("<value>خواندن دیسک</value>", persian, StringComparison.Ordinal);
        Assert.Contains("<value>دریافت شبکه</value>", persian, StringComparison.Ordinal);
        string[] overviewPersian = XDocument.Parse(persian).Root!
            .Elements("data")
            .Where(element => (element.Attribute("name")?.Value ?? string.Empty)
                .StartsWith("SystemOverview", StringComparison.Ordinal))
            .Select(element => element.Element("value")?.Value ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(overviewPersian);
        Assert.All(overviewPersian, value => Assert.DoesNotContain("ناقص", value, StringComparison.Ordinal));
    }

    [Fact]
    public void RapidMetricsAreNotScreenReaderLiveRegionsAndChartUpdatesAreCoalesced()
    {
        string root = FindRepositoryRoot();
        string overview = Read(root, "SystemOverviewPage.xaml");
        string main = Read(root, "MainWindow.xaml");
        string chart = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "Controls", "MetricSparkline.xaml.cs"));

        Assert.DoesNotMatch(
            "AutomationProperties\\.LiveSetting=\"Polite\"[\\s\\S]{0,80}FlowDirection=\"LeftToRight\"",
            overview);
        Assert.DoesNotMatch(
            "RunningAppsTitle[\\s\\S]{0,180}AutomationProperties\\.LiveSetting",
            main);
        Assert.Contains("OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRedraw()", chart, StringComparison.Ordinal);
    }

    private static string Read(string root, string file) =>
        File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", file));

    private static int Count(string value, string search)
    {
        int count = 0;
        for (int index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
        {
            count++;
        }

        return count;
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
