using MonitoringXS.App.Appearance;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class AppearancePreferenceTests
{
    [Fact]
    public void HighContrastAlwaysUsesTheSystemThemeWithoutChangingThePreference()
    {
        AppearanceThemeChoice result = AppearanceThemeResolver.Resolve(
            ApplicationTheme.Dark,
            highContrast: true);

        Assert.Equal(AppearanceThemeChoice.System, result);
    }
}
