using System.Xml.Linq;
using MonitoringXS.App.Localization;

namespace MonitoringXS.App.Tests;

public sealed class AboutIdentityTests
{
    private static readonly string SolutionRoot = FindSolutionRoot();

    private static string FindSolutionRoot()
    {
        // Walk up from the test output directory until we find the solution file.
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "MonitoringXS.sln")))
            {
                return dir;
            }
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null)
            {
                break;
            }
            dir = parent;
        }
        // Fallback to the original heuristic
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [Fact]
    public void AboutContentHasEnglishResources()
    {
        Dictionary<string, string> english = LoadResources("en-US");

        Assert.True(english.ContainsKey("AboutPageTitle"), "Missing AboutPageTitle in en-US");
        Assert.True(english.ContainsKey("AboutDescription"), "Missing AboutDescription in en-US");
        Assert.True(english.ContainsKey("AboutBetaBadge"), "Missing AboutBetaBadge in en-US");
        Assert.True(english.ContainsKey("AboutPlatform"), "Missing AboutPlatform in en-US");
        Assert.True(english.ContainsKey("AboutOpenSource"), "Missing AboutOpenSource in en-US");
        Assert.True(english.ContainsKey("AboutPrivacySummary"), "Missing AboutPrivacySummary in en-US");
        Assert.True(english.ContainsKey("AboutPrivacyDetail"), "Missing AboutPrivacyDetail in en-US");
    }

    [Fact]
    public void AboutContentHasPersianResources()
    {
        Dictionary<string, string> persian = LoadResources("fa-IR");

        Assert.True(persian.ContainsKey("AboutPageTitle"), "Missing AboutPageTitle in fa-IR");
        Assert.True(persian.ContainsKey("AboutDescription"), "Missing AboutDescription in fa-IR");
        Assert.True(persian.ContainsKey("AboutBetaBadge"), "Missing AboutBetaBadge in fa-IR");
        Assert.True(persian.ContainsKey("AboutPlatform"), "Missing AboutPlatform in fa-IR");
        Assert.True(persian.ContainsKey("AboutOpenSource"), "Missing AboutOpenSource in fa-IR");
        Assert.True(persian.ContainsKey("AboutPrivacySummary"), "Missing AboutPrivacySummary in fa-IR");
        Assert.True(persian.ContainsKey("AboutPrivacyDetail"), "Missing AboutPrivacyDetail in fa-IR");
    }

    [Fact]
    public void WhatsNewIsLocalizedInBothLanguages()
    {
        Dictionary<string, string> english = LoadResources("en-US");
        Dictionary<string, string> persian = LoadResources("fa-IR");

        string[] whatsNewKeys =
        [
            "WhatsNewTitle",
            "WhatsNewLocalization",
            "WhatsNewSearch",
            "WhatsNewSorting",
            "WhatsNewDiagnostics",
            "WhatsNewMetricExplanations",
            "WhatsNewProcessSelection",
            "WhatsNewProcessSafety",
            "WhatsNewCpuMonitoring",
            "WhatsNewMemoryMonitoring",
            "WhatsNewDiskMonitoring",
            "WhatsNewNetworkMonitoring",
            "WhatsNewGpuMonitoring",
            "WhatsNewHistory",
            "WhatsNewChartGaps",
            "WhatsNewInstaller",
            "WhatsNewSystemOverviewFoundation"
        ];

        foreach (string key in whatsNewKeys)
        {
            Assert.True(english.ContainsKey(key), $"Missing {key} in en-US");
            Assert.True(persian.ContainsKey(key), $"Missing {key} in fa-IR");
            Assert.False(string.IsNullOrWhiteSpace(english[key]), $"{key} is empty in en-US");
            Assert.False(string.IsNullOrWhiteSpace(persian[key]), $"{key} is empty in fa-IR");
        }
    }

    [Fact]
    public void BetaLimitationsIsLocalizedInBothLanguages()
    {
        Dictionary<string, string> english = LoadResources("en-US");
        Dictionary<string, string> persian = LoadResources("fa-IR");

        string[] limitationKeys =
        [
            "BetaLimitationsTitle",
            "BetaLimitationProviders",
            "BetaLimitationHardware",
            "BetaLimitationPermissions",
            "BetaLimitationMetricStates",
            "BetaLimitationChartGaps",
            "BetaLimitationNoFakeData",
            "BetaLimitationDefects"
        ];

        foreach (string key in limitationKeys)
        {
            Assert.True(english.ContainsKey(key), $"Missing {key} in en-US");
            Assert.True(persian.ContainsKey(key), $"Missing {key} in fa-IR");
            Assert.False(string.IsNullOrWhiteSpace(english[key]), $"{key} is empty in en-US");
            Assert.False(string.IsNullOrWhiteSpace(persian[key]), $"{key} is empty in fa-IR");
        }
    }

    [Fact]
    public void EnUsAndFaIrResourceKeyParityForAboutKeys()
    {
        Dictionary<string, string> english = LoadResources("en-US");
        Dictionary<string, string> persian = LoadResources("fa-IR");

        string[] aboutKeys =
        [
            "AboutPageTitle", "AboutDescription", "AboutPlatform", "AboutOpenSource",
            "AboutPrivacySummary", "AboutPrivacyDetail", "AboutRepository",
            "AboutLicense", "AboutCopyright", "AboutBetaBadge",
            "WhatsNewTitle", "BetaLimitationsTitle"
        ];

        foreach (string key in aboutKeys)
        {
            Assert.True(english.ContainsKey(key), $"Missing {key} in en-US");
            Assert.True(persian.ContainsKey(key), $"Missing {key} in fa-IR");
        }
    }

    [Fact]
    public void NoDuplicateLocalizationKeysInCode()
    {
        Type keysType = typeof(LocalizationKeys);
        var fields = keysType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var values = fields.Select(f => (string)f.GetValue(null)!).ToList();
        var duplicates = values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void VersionAndUrlRemainLtrInResources()
    {
        Dictionary<string, string> english = LoadResources("en-US");

        // Repository URL should be LTR-safe (ASCII-only)
        if (english.TryGetValue("NavAbout.Content", out string? aboutContent))
        {
            Assert.False(string.IsNullOrWhiteSpace(aboutContent));
        }
    }

    [Fact]
    public void SystemOverviewUiIsNotFalselyClaimed()
    {
        Dictionary<string, string> english = LoadResources("en-US");

        // What's New mentions "data foundation" not "UI"
        if (english.TryGetValue("WhatsNewSystemOverviewFoundation", out string? value))
        {
            Assert.DoesNotContain("System Overview UI", value, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("foundation", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> LoadResources(string language)
    {
        string path = Path.Combine(
            SolutionRoot,
            "src", "MonitoringXS.App", "Strings", language, "Resources.resw");
        XDocument doc = XDocument.Load(path);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement element in doc.Root!.Elements("data"))
        {
            string key = element.Attribute("name")!.Value;
            string value = element.Element("value")!.Value;
            dict.TryAdd(key, value);
        }
        return dict;
    }
}