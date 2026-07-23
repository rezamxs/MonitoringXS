namespace MonitoringXS.App.Appearance;

public enum AppearanceMode
{
    System,
    Light,
    Dark
}

public enum AppearanceThemeChoice
{
    System,
    Light,
    Dark
}

public sealed record AppearanceOption(AppearanceMode Mode, string Label);

public static class AppearancePreferenceSerializer
{
    public static AppearanceMode Parse(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out AppearanceMode mode) && Enum.IsDefined(mode)
            ? mode
            : AppearanceMode.System;

    public static string Serialize(AppearanceMode mode) => mode.ToString();
}

public static class AppearanceThemeResolver
{
    public static AppearanceThemeChoice Resolve(AppearanceMode preference, bool highContrast) =>
        highContrast
            ? AppearanceThemeChoice.System
            : preference switch
            {
                AppearanceMode.Light => AppearanceThemeChoice.Light,
                AppearanceMode.Dark => AppearanceThemeChoice.Dark,
                _ => AppearanceThemeChoice.System
            };
}

internal static class AppearancePresentation
{
    public static string ResolvedStateLabel(bool isDark) =>
        isDark ? "Currently Dark" : "Currently Light";
}
