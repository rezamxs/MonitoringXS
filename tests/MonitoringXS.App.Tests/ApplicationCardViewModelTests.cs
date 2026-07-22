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
