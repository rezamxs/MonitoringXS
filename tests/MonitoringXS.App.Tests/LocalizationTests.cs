using System.Globalization;
using System.Xml.Linq;
using MonitoringXS.App.Localization;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EnglishAndPersianResourcesHaveExactNonEmptyParity()
    {
        (Dictionary<string, string> english, Dictionary<string, string> persian) = LoadResources();

        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(), persian.Keys.Order());
        Assert.All(english.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.All(persian.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void LanguageSwitchIsImmediateAndPersianIsRightToLeft()
    {
        LocalizationService service = CreateService("en-US");
        int notifications = 0;
        service.LanguageChanged += (_, _) => notifications++;

        service.SetLanguage(ApplicationLanguage.Persian);

        Assert.Equal("fa-IR", service.Culture.Name);
        Assert.Equal(TextDirection.RightToLeft, service.Direction);
        Assert.Equal("تنظیمات", service.Get("SettingsPageTitle.Text"));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void EnglishAndInvalidLanguageUseSafeLeftToRightFallback()
    {
        LocalizationService service = CreateService("de-DE");

        service.SetLanguage((ApplicationLanguage)999);

        Assert.Equal(ApplicationLanguage.System, service.Language);
        Assert.Equal("en-US", service.Culture.Name);
        Assert.Equal(TextDirection.LeftToRight, service.Direction);
        Assert.Equal("Settings", service.Get("SettingsPageTitle.Text"));
    }

    [Fact]
    public void ResourceFormattingUsesActiveCultureAndWholeMessages()
    {
        LocalizationService service = CreateService("en-US");
        service.SetLanguage(ApplicationLanguage.Persian);

        string message = service.Format(LocalizationKeys.EndTaskMessage, "App", "app.exe", 42);

        Assert.Contains("App", message, StringComparison.Ordinal);
        Assert.Contains("app.exe", message, StringComparison.Ordinal);
        Assert.Contains("42", message, StringComparison.Ordinal);
        Assert.Contains("پایان", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionXamlUsesLocalizedUidsAndNarrowLtrBoundaries()
    {
        string root = FindRepositoryRoot();
        string allXaml = string.Join('\n', new[]
        {
            "MainWindow.xaml", "HistoryPage.xaml", "SettingsPage.xaml", "DiagnosticsPage.xaml",
            Path.Combine("Controls", "MetricSparkline.xaml")
        }.Select(path => File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", path))));

        Assert.True(Count(allXaml, "x:Uid=") >= 45);
        Assert.Contains("FlowDirection=\"LeftToRight\"", allXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Metric", allXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Physical disk (ETW)", allXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Network (ETW)", allXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizedAutomationNamesExistInBothLanguages()
    {
        (Dictionary<string, string> english, Dictionary<string, string> persian) = LoadResources();
        string[] automationKeys = english.Keys
            .Where(key => key.EndsWith("AutomationProperties.Name", StringComparison.Ordinal))
            .ToArray();

        Assert.True(automationKeys.Length >= 15);
        Assert.All(automationKeys, key => Assert.True(persian.ContainsKey(key)));
    }

    [Fact]
    public void LongPersianSurfacesWrapAndRemainScrollableAtSupportedScales()
    {
        string root = FindRepositoryRoot();
        string settings = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "SettingsPage.xaml"));
        string history = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "HistoryPage.xaml"));

        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", settings, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", settings, StringComparison.Ordinal);
        Assert.Contains("MinItemWidth=\"400\"", history, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"100\"", settings, StringComparison.Ordinal);
    }

    private static LocalizationService CreateService(string systemCulture) => new(
        Path.Combine(FindRepositoryRoot(), "src", "MonitoringXS.App"),
        CultureInfo.GetCultureInfo(systemCulture));

    private static (Dictionary<string, string> English, Dictionary<string, string> Persian) LoadResources()
    {
        string strings = Path.Combine(FindRepositoryRoot(), "src", "MonitoringXS.App", "Strings");
        return (
            Load(Path.Combine(strings, "en-US", "Resources.resw")),
            Load(Path.Combine(strings, "fa-IR", "Resources.resw")));
    }

    private static Dictionary<string, string> Load(string path) => XDocument.Load(path)
        .Root!
        .Elements("data")
        .ToDictionary(
            element => element.Attribute("name")!.Value,
            element => element.Element("value")!.Value,
            StringComparer.Ordinal);

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
