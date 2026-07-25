using System.ComponentModel;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class ApplicationCardViewModelTests
{
    [Fact]
    public void AutomationNameChangesWhenCardMetricsAndAvailabilityChange()
    {
        ApplicationCardViewModel card = new()
        {
            LogicalApplicationId = "sample-app",
            Disposition = ApplicationDisposition.Installed
        };
        card.Update(Snapshot(
            MetricValue<double>.Unavailable(MetricAvailability.WarmingUp),
            MetricValue<long>.Available(128 * 1024 * 1024),
            MetricValue<double>.Unavailable(MetricAvailability.WarmingUp),
            MetricValue<double>.Unavailable(MetricAvailability.WarmingUp)), []);
        string warmingUpName = card.AutomationName;
        List<string?> changedProperties = [];
        card.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        card.Update(Snapshot(
            MetricValue<double>.Available(12.5),
            MetricValue<long>.Available(256 * 1024 * 1024),
            MetricValue<double>.Available(2048),
            MetricValue<double>.Available(1024)), []);

        Assert.NotEqual(warmingUpName, card.AutomationName);
        Assert.Contains(nameof(ApplicationCardViewModel.AutomationName), changedProperties);
        Assert.Contains("Sample App", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Running", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("CPU 12.5%", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Memory 256 MB", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Process I/O 2.0 KB/s read", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Physical disk Access denied", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Network Access denied", card.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsesMatchingDeniedDirectionsIntoOneHonestVisualState()
    {
        ApplicationCardViewModel card = Card();

        card.Update(Snapshot(
            MetricValue<double>.Available(1),
            MetricValue<long>.Available(128 * 1024 * 1024),
            MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<double>.Unavailable(MetricAvailability.AccessDenied)), []);

        Assert.Equal("Unavailable", card.IoText);
        Assert.Equal("Access denied", card.IoStatusText);
        Assert.Equal("Unavailable", card.PhysicalDiskText);
        Assert.Equal("Access denied", card.PhysicalDiskStatusText);
        Assert.Equal("Unavailable", card.NetworkText);
        Assert.Equal("Access denied", card.NetworkStatusText);
        Assert.DoesNotContain("Unavailable read", card.PhysicalDiskText, StringComparison.Ordinal);
        Assert.DoesNotContain("Unavailable receive", card.NetworkText, StringComparison.Ordinal);
        Assert.Contains("Process I/O Access denied", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Physical disk Access denied", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Network Access denied", card.AutomationName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MetricAvailability.WarmingUp, "Warming up", "")]
    [InlineData(MetricAvailability.Unavailable, "Unavailable", "")]
    [InlineData(MetricAvailability.Unsupported, "Unavailable", "Unsupported")]
    public void KeepsUnavailableReasonsVisibleWithoutRepeatingDirections(
        MetricAvailability availability,
        string expectedValue,
        string expectedStatus)
    {
        ApplicationCardViewModel card = Card();
        MetricValue<double> unavailable = MetricValue<double>.Unavailable(availability);
        ApplicationMetricSnapshot snapshot = Snapshot(
            MetricValue<double>.Available(1),
            MetricValue<long>.Available(128 * 1024 * 1024),
            unavailable,
            unavailable) with
        {
            PhysicalDisk = PhysicalDiskMetricSet.Unavailable(availability, expectedStatus),
            Network = NetworkMetricSet.Unavailable(
                availability,
                NetworkAvailabilityReason.None,
                expectedStatus)
        };

        card.Update(snapshot, []);

        Assert.Equal(expectedValue, card.IoText);
        Assert.Equal(expectedStatus, card.IoStatusText);
        Assert.Equal(expectedValue, card.PhysicalDiskText);
        Assert.Equal(expectedStatus, card.PhysicalDiskStatusText);
        Assert.Equal(expectedValue, card.NetworkText);
        Assert.Equal(expectedStatus, card.NetworkStatusText);
    }

    [Fact]
    public void PartialPairsKeepMeasuredLowerBoundsAndExposePartialStatus()
    {
        ApplicationCardViewModel card = Card();
        MetricValue<double> read = MetricValue<double>.Partial(2048, "Lower bound.");
        MetricValue<double> write = MetricValue<double>.Partial(1024, "Lower bound.");
        ApplicationMetricSnapshot snapshot = Snapshot(
            MetricValue<double>.Partial(4, "Lower bound."),
            MetricValue<long>.Partial(128 * 1024 * 1024, "Lower bound."),
            read,
            write) with
        {
            PhysicalDisk = new PhysicalDiskMetricSet(
                read,
                write,
                MetricValue<ulong>.Partial(0, "Lower bound."),
                MetricValue<ulong>.Partial(0, "Lower bound."),
                MetricValue<ulong>.Partial(0, "Lower bound."),
                MetricValue<ulong>.Partial(0, "Lower bound."),
                default),
            Network = new NetworkMetricSet(
                read,
                write,
                MetricValue<ulong>.Partial(0, "Lower bound."),
                MetricValue<ulong>.Partial(0, "Lower bound."),
                MetricValue<int>.Partial(0, "Lower bound."),
                MetricValue<int>.Partial(0, "Lower bound."),
                default)
        };

        card.Update(snapshot, []);

        Assert.StartsWith("\u2265 2.0 KB/s read", card.IoText, StringComparison.Ordinal);
        Assert.Equal("Partial · lower bound", card.IoStatusText);
        Assert.Equal("Partial · lower bound", card.PhysicalDiskStatusText);
        Assert.Equal("Partial · lower bound", card.NetworkStatusText);
        Assert.Contains("receive", card.NetworkText, StringComparison.Ordinal);
        Assert.Contains("send", card.NetworkText, StringComparison.Ordinal);
        Assert.Contains("Network", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("receive", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("send", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("at least", card.AutomationName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial lower bound", card.AutomationName, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationCardViewModel Card() => new()
    {
        LogicalApplicationId = "sample-app",
        Disposition = ApplicationDisposition.Installed
    };

    private static ApplicationMetricSnapshot Snapshot(
        MetricValue<double> cpu,
        MetricValue<long> memory,
        MetricValue<double> ioRead,
        MetricValue<double> ioWrite) => new(
        new ApplicationIdentity(
            "sample-app",
            "Sample App",
            "Sample Publisher",
            ApplicationDisposition.Installed,
            @"C:\Apps\Sample",
            ClassificationConfidence.High,
            "test"),
        DateTimeOffset.UtcNow,
        cpu,
        memory,
        ioRead,
        ioWrite,
        MetricValue<ulong>.Available(0),
        MetricValue<ulong>.Available(0),
        MetricValue<ulong>.Available(0),
        MetricValue<ulong>.Available(0),
        1,
        [])
    {
        PhysicalDisk = PhysicalDiskMetricSet.Unavailable(
            MetricAvailability.AccessDenied,
            "Access denied."),
        Network = NetworkMetricSet.Unavailable(
            MetricAvailability.AccessDenied,
            NetworkAvailabilityReason.AccessDenied,
            "Access denied.")
    };
}
