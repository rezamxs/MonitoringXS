using System.Globalization;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class MetricExplanationServiceTests
{
    [Fact]
    public void EverySupportedMetricHasEnglishAndPersianContentWithoutBeginnerEtw()
    {
        LocalizationService localization = CreateLocalization();
        MetricExplanationService service = new(localization);
        IReadOnlyList<MetricExplanationItem> english = service.Create(Snapshot(), 1);

        localization.SetLanguage(ApplicationLanguage.Persian);
        IReadOnlyList<MetricExplanationItem> persian = service.Create(Snapshot(), 1);

        Assert.Equal(9, english.Count);
        Assert.Equal(9, persian.Count);
        Assert.All(english.Concat(persian), item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.BeginnerText));
            Assert.False(string.IsNullOrWhiteSpace(item.AdvancedText));
            Assert.False(string.IsNullOrWhiteSpace(item.StatusText));
            Assert.DoesNotContain("ETW", item.BeginnerText, StringComparison.OrdinalIgnoreCase);
        });
        Assert.NotEqual(english[0].BeginnerText, persian[0].BeginnerText);
    }

    [Theory]
    [InlineData(MetricAvailability.Partial, null, NetworkAvailabilityReason.None, GpuAvailabilityReason.None, "lower bound")]
    [InlineData(MetricAvailability.WarmingUp, null, NetworkAvailabilityReason.None, GpuAvailabilityReason.None, "another valid sample")]
    [InlineData(MetricAvailability.Unavailable, "Broker service stopped.", NetworkAvailabilityReason.None, GpuAvailabilityReason.None, "service is stopped")]
    [InlineData(MetricAvailability.Unavailable, null, NetworkAvailabilityReason.SessionConflict, GpuAvailabilityReason.None, "session conflicts")]
    [InlineData(MetricAvailability.Partial, null, NetworkAvailabilityReason.EventLoss, GpuAvailabilityReason.None, "samples were lost")]
    [InlineData(MetricAvailability.Unavailable, null, NetworkAvailabilityReason.None, GpuAvailabilityReason.ProcessExited, "process exited")]
    [InlineData(MetricAvailability.Unsupported, null, NetworkAvailabilityReason.None, GpuAvailabilityReason.UnsupportedDriver, "driver does not expose")]
    [InlineData(MetricAvailability.Error, null, NetworkAvailabilityReason.None, GpuAvailabilityReason.None, "collector reported an error")]
    public void MapsOnlyRealAvailabilityAndReasonStatesPrecisely(
        MetricAvailability availability,
        string? detail,
        NetworkAvailabilityReason networkReason,
        GpuAvailabilityReason gpuReason,
        string expected)
    {
        MetricExplanationService service = new(CreateLocalization());

        string result = service.Reason(
            MetricDescriptionId.Network,
            availability,
            detail,
            networkReason,
            gpuReason);

        Assert.Contains(expected, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TechnicalProviderIdentifiersRemainUnformattedAcrossCultures()
    {
        LocalizationService localization = CreateLocalization();
        MetricExplanationService service = new(localization);
        string english = service.Create(Snapshot(), 1)
            .Single(item => item.Name == "GPU utilization")
            .ProviderName;

        localization.SetLanguage(ApplicationLanguage.Persian);
        string persian = service.Create(Snapshot(), 1)[5].ProviderName;

        Assert.Equal(GpuCollectorDiagnostics.WindowsPdhProvider, english);
        Assert.Equal(english, persian);
    }

    [Fact]
    public void HistoryErrorMapsToDatabaseUnavailableWithoutFakeZero()
    {
        MetricExplanationService service = new(CreateLocalization());

        string result = service.Reason(
            MetricDescriptionId.History,
            MetricAvailability.Error);

        Assert.Equal("The local history database is unavailable.", result);
        Assert.DoesNotContain("0", result, StringComparison.Ordinal);
    }

    private static LocalizationService CreateLocalization() => new(
        Path.Combine(FindRepositoryRoot(), "src", "MonitoringXS.App"),
        CultureInfo.GetCultureInfo("en-US"));

    private static ApplicationMetricSnapshot Snapshot() => new(
        new ApplicationIdentity(
            "sample",
            "Sample",
            "Publisher",
            ApplicationDisposition.Installed,
            null,
            ClassificationConfidence.High,
            "test"),
        DateTimeOffset.UtcNow,
        MetricValue<double>.Available(1),
        MetricValue<long>.Available(1),
        MetricValue<double>.Available(1),
        MetricValue<double>.Available(1),
        MetricValue<ulong>.Available(1),
        MetricValue<ulong>.Available(1),
        MetricValue<ulong>.Available(1),
        MetricValue<ulong>.Available(1),
        1,
        [])
    {
        PhysicalDisk = new(
            MetricValue<double>.Available(1),
            MetricValue<double>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            new PhysicalDiskCollectorDiagnostics(0, 0, 0, 0, CollectorStatus: MetricAvailability.Available)),
        Network = new(
            MetricValue<double>.Available(1),
            MetricValue<double>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<int>.Available(1),
            MetricValue<int>.Available(1),
            new NetworkCollectorDiagnostics { CollectorStatus = MetricAvailability.Available }),
        Gpu = new(
            MetricValue<double>.Available(1),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            null,
            new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Available
            })
    };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MonitoringXS.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
