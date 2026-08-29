using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Appearance;

public enum AppearanceThemeChoice
{
    System,
    Light,
    Dark
}

public static class AppearanceThemeResolver
{
    public static AppearanceThemeChoice Resolve(ApplicationTheme preference, bool highContrast) =>
        highContrast
            ? AppearanceThemeChoice.System
            : preference switch
            {
                ApplicationTheme.Light => AppearanceThemeChoice.Light,
                ApplicationTheme.Dark => AppearanceThemeChoice.Dark,
                _ => AppearanceThemeChoice.System
            };
}
