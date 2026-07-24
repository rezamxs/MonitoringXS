using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class ApplicationTabViewModelTests
{
    [Fact]
    public void PhysicalDiskDiagnosticsDoNotCallUnavailableCollectionComplete()
    {
        ApplicationMetricSnapshot snapshot = Snapshot() with
        {
            PhysicalDisk = new PhysicalDiskMetricSet(
                MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
                new PhysicalDiskCollectorDiagnostics(
                    0,
                    0,
                    0,
                    0,
                    SessionStartFailures: 2,
                    AccessDeniedFailures: 2,
                    CollectorStatus: MetricAvailability.AccessDenied))
        };
        ApplicationTabViewModel tab = new("sample-app", "Sample App");

        tab.Update(snapshot, []);

        Assert.Contains("Status AccessDenied", tab.PhysicalDiskDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("session start failures 2", tab.PhysicalDiskDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("access denied 2", tab.PhysicalDiskDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("completeness unavailable", tab.PhysicalDiskDiagnosticsText, StringComparison.Ordinal);
        Assert.DoesNotContain("completeness complete", tab.PhysicalDiskDiagnosticsText, StringComparison.Ordinal);
    }

    private static ApplicationMetricSnapshot Snapshot() => new(
        new ApplicationIdentity(
            "sample-app",
            "Sample App",
            "Sample Publisher",
            ApplicationDisposition.Installed,
            @"C:\Apps\Sample",
            ClassificationConfidence.High,
            "test"),
        DateTimeOffset.UtcNow,
        MetricValue<double>.Available(1),
        MetricValue<long>.Available(128 * 1024 * 1024),
        MetricValue<double>.Available(10),
        MetricValue<double>.Available(20),
        MetricValue<ulong>.Available(100),
        MetricValue<ulong>.Available(200),
        MetricValue<ulong>.Available(1),
        MetricValue<ulong>.Available(2),
        1,
        []);
}
