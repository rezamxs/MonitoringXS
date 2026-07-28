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

    [Fact]
    public void NetworkDiagnosticsExposeUnavailableReasonWithoutCallingItComplete()
    {
        ApplicationMetricSnapshot snapshot = Snapshot() with
        {
            Network = new NetworkMetricSet(
                MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<ulong>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<int>.Unavailable(MetricAvailability.AccessDenied),
                MetricValue<int>.Unavailable(MetricAvailability.AccessDenied),
                new NetworkCollectorDiagnostics
                {
                    Reason = NetworkAvailabilityReason.AccessDenied,
                    CollectorStatus = MetricAvailability.AccessDenied,
                    CollectorStatusReason = "Administrator access is required.",
                    SessionStartFailures = 1,
                    AccessDeniedFailures = 1
                })
        };
        ApplicationTabViewModel tab = new("sample-app", "Sample App");

        tab.Update(snapshot, []);

        Assert.Contains("Status AccessDenied", tab.NetworkDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("reason AccessDenied", tab.NetworkDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("detail Administrator access is required.", tab.NetworkDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("completeness unavailable", tab.NetworkDiagnosticsText, StringComparison.Ordinal);
        Assert.DoesNotContain("completeness complete", tab.NetworkDiagnosticsText, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuStatusAndDiagnosticsExposeIndependentCounterCompleteness()
    {
        ApplicationMetricSnapshot snapshot = Snapshot() with
        {
            Gpu = new GpuMetricSet(
                MetricValue<double>.Available(25),
                MetricValue<ulong>.Unavailable(MetricAvailability.Unsupported),
                MetricValue<ulong>.Partial(1024, "One adapter was unavailable."),
                new GpuEngineId(1, 0, 2, "3D"),
                new GpuCollectorDiagnostics
                {
                    ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                    CollectorStatus = MetricAvailability.Partial,
                    Reason = GpuAvailabilityReason.CounterUnavailable,
                    UtilizationCounterStatus = MetricAvailability.Available,
                    DedicatedMemoryCounterStatus = MetricAvailability.Unsupported,
                    SharedMemoryCounterStatus = MetricAvailability.Partial,
                    FirstObservationCounterSamplesRejected = 2,
                    QuarantinedUtilizationSamples = 1,
                    QuarantinedDedicatedMemorySamples = 2,
                    QuarantinedSharedMemorySamples = 3,
                    OutsideApplicationSetCounterInstances = 3,
                    ExitedProcessCounterInstances = 2,
                    MalformedCounterInstances = 1,
                    DuplicateCounterInstances = 4
                })
        };
        ApplicationTabViewModel tab = new("sample-app", "Sample App");

        tab.Update(snapshot, []);

        Assert.Contains("Utilization Available", tab.GpuStatusText, StringComparison.Ordinal);
        Assert.Contains("dedicated memory Unsupported", tab.GpuStatusText, StringComparison.Ordinal);
        Assert.Contains("shared memory Partial", tab.GpuStatusText, StringComparison.Ordinal);
        Assert.Contains("outside application set 3", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("exited process counters 2", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("malformed instances 1", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("duplicate instances 4", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("first-observation counters rejected 2", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("quarantined utilization 1", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("dedicated memory 2", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("shared memory 3", tab.GpuDiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("not unique physical ownership", tab.GpuDiagnosticsText, StringComparison.Ordinal);
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
