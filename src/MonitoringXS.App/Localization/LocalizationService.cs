using System.Globalization;
using System.Xml.Linq;
using System.Reflection;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Localization;

public enum TextDirection
{
    LeftToRight,
    RightToLeft
}

public sealed class LanguageChangedEventArgs(
    ApplicationLanguage language,
    CultureInfo culture,
    TextDirection direction) : EventArgs
{
    public ApplicationLanguage Language { get; } = language;

    public CultureInfo Culture { get; } = culture;

    public TextDirection Direction { get; } = direction;
}

public sealed class LocalizationService
{
    private const string EnglishCultureName = "en-US";
    private const string PersianCultureName = "fa-IR";
    private readonly Dictionary<string, string> _english;
    private readonly Dictionary<string, string> _persian;
    private readonly CultureInfo _systemCulture;

    public LocalizationService()
        : this(AppContext.BaseDirectory, CultureInfo.CurrentUICulture)
    {
    }

    internal LocalizationService(string baseDirectory, CultureInfo systemCulture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(systemCulture);
        _english = Load(Path.Combine(baseDirectory, "Strings", EnglishCultureName, "Resources.resw"), EnglishCultureName);
        _persian = Load(Path.Combine(baseDirectory, "Strings", PersianCultureName, "Resources.resw"), PersianCultureName);
        ValidateResources(_english, _persian);
        _systemCulture = systemCulture;
        Language = ApplicationLanguage.System;
        Culture = ResolveCulture(Language);
    }

    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    public ApplicationLanguage Language { get; private set; }

    public CultureInfo Culture { get; private set; }

    public TextDirection Direction => Culture.TextInfo.IsRightToLeft
        ? TextDirection.RightToLeft
        : TextDirection.LeftToRight;

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Dictionary<string, string> active =
            Culture.Name.Equals(PersianCultureName, StringComparison.OrdinalIgnoreCase)
                ? _persian
                : _english;
        if (active.TryGetValue(key, out string? value)
            || _english.TryGetValue(key, out value))
        {
            return value;
        }

        throw new KeyNotFoundException($"Missing production localization resource: {key}");
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    public void SetLanguage(ApplicationLanguage language)
    {
        if (!Enum.IsDefined(language))
        {
            language = ApplicationLanguage.System;
        }

        CultureInfo culture = ResolveCulture(language);
        if (Language == language && Culture.Name == culture.Name)
        {
            return;
        }

        Language = language;
        Culture = culture;
        LanguageChanged?.Invoke(this, new(language, culture, Direction));
    }

    private CultureInfo ResolveCulture(ApplicationLanguage language) => language switch
    {
        ApplicationLanguage.Persian => CultureInfo.GetCultureInfo(PersianCultureName),
        ApplicationLanguage.English => CultureInfo.GetCultureInfo(EnglishCultureName),
        _ when _systemCulture.Name.StartsWith("fa", StringComparison.OrdinalIgnoreCase) =>
            CultureInfo.GetCultureInfo(PersianCultureName),
        _ => CultureInfo.GetCultureInfo(EnglishCultureName)
    };

    private static Dictionary<string, string> Load(string path, string culture)
    {
        XDocument document;
        if (File.Exists(path))
        {
            document = XDocument.Load(path, LoadOptions.None);
        }
        else
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                $"MonitoringXS.App.Strings.{culture}.Resources.resw")
                ?? throw new FileNotFoundException($"Localization resource not found: {path}", path);
            document = XDocument.Load(stream, LoadOptions.None);
        }
        return document.Root?.Elements("data").ToDictionary(
            element => (string?)element.Attribute("name")
                ?? throw new InvalidDataException($"Unnamed localization resource in {path}."),
            element => element.Element("value")?.Value
                ?? throw new InvalidDataException($"Empty localization resource in {path}."),
            StringComparer.Ordinal)
            ?? throw new InvalidDataException($"Invalid localization resource file: {path}");
    }

    private static void ValidateResources(
        IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> persian)
    {
        string[] missingPersian = english.Keys.Except(persian.Keys, StringComparer.Ordinal).ToArray();
        string[] missingEnglish = persian.Keys.Except(english.Keys, StringComparer.Ordinal).ToArray();
        string[] empty = english.Concat(persian)
            .Where(item => string.IsNullOrWhiteSpace(item.Value))
            .Select(item => item.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missingPersian.Length > 0 || missingEnglish.Length > 0 || empty.Length > 0)
        {
            throw new InvalidDataException(
                $"Localization resources are invalid. Missing fa-IR: {string.Join(", ", missingPersian)}; missing en-US: {string.Join(", ", missingEnglish)}; empty: {string.Join(", ", empty)}.");
        }
    }
}
