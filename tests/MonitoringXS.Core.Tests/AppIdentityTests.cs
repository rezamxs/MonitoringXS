using System.Reflection;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Tests;

public sealed class AppIdentityTests
{
    [Fact]
    public void DisplayVersionExistsAndIsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppIdentity.DisplayVersion));
    }

    [Fact]
    public void NumericVersionIsValidSemVer()
    {
        string version = AppIdentity.NumericVersion;
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(Version.TryParse(version, out _), $"NumericVersion '{version}' is not a valid version.");
    }

    [Fact]
    public void PublicBetaLabelExists()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppIdentity.BetaChannel));
        Assert.Contains("Beta", AppIdentity.BetaChannel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductNameIsMonitoringXS()
    {
        Assert.Equal("Monitoring XS", AppIdentity.ProductName);
    }

    [Fact]
    public void RepositoryUrlIsValidHttps()
    {
        Assert.StartsWith("https://", AppIdentity.RepositoryUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void LicenseIsMit()
    {
        Assert.Equal("MIT", AppIdentity.License);
    }
}