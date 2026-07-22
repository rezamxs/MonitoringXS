using MonitoringXS.App.Appearance;

namespace MonitoringXS.App.Tests;

public sealed class AppearancePreferenceTests
{
    [Theory]
    [InlineData("System", AppearanceMode.System)]
    [InlineData("Light", AppearanceMode.Light)]
    [InlineData("Dark", AppearanceMode.Dark)]
    public void ParseAcceptsTheThreeSupportedModes(string value, AppearanceMode expected) =>
        Assert.Equal(expected, AppearancePreferenceSerializer.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HighContrast")]
    [InlineData("unknown")]
    public void ParseFallsBackToSystemForMissingOrUnknownValues(string? value) =>
        Assert.Equal(AppearanceMode.System, AppearancePreferenceSerializer.Parse(value));

    [Fact]
    public void HighContrastAlwaysUsesTheSystemThemeWithoutChangingThePreference()
    {
        AppearanceThemeChoice result = AppearanceThemeResolver.Resolve(AppearanceMode.Dark, highContrast: true);

        Assert.Equal(AppearanceThemeChoice.System, result);
    }
}
