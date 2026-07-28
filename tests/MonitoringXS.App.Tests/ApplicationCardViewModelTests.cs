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
        Assert.Contains("GPU Access denied", card.AutomationName, StringComparison.Ordinal);
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
        Assert.Contains("GPU Access denied", card.AutomationName, StringComparison.Ordinal);
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

    [Fact]
    public void ExposesGpuUtilizationAndMemoryInAccessibleCardText()
    {
        ApplicationCardViewModel card = Card();
        ApplicationMetricSnapshot snapshot = Snapshot(
            MetricValue<double>.Available(1),
            MetricValue<long>.Available(128 * 1024 * 1024),
            MetricValue<double>.Available(0),
            MetricValue<double>.Available(0)) with
        {
            Gpu = new GpuMetricSet(
                MetricValue<double>.Available(42.5),
                MetricValue<ulong>.Available(64 * 1024 * 1024),
                MetricValue<ulong>.Available(8 * 1024 * 1024),
                new GpuEngineId(1, 0, 0, "3D"),
                new GpuCollectorDiagnostics
                {
                    ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                    CollectorStatus = MetricAvailability.Available,
                    Reason = GpuAvailabilityReason.None
                })
        };

        card.Update(snapshot, []);

        Assert.Equal("42.5%", card.GpuText);
        Assert.Equal("64.0 MB dedicated · 8.0 MB shared", card.GpuStatusText);
        Assert.Contains("GPU 42.5%", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("64.0 MB dedicated", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains("8.0 MB shared", card.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void ExposesAvailableGpuUtilizationWithUnavailableMemoryHonestly()
    {
        ApplicationCardViewModel card = Card();
        ApplicationMetricSnapshot snapshot = Snapshot(
            MetricValue<double>.Available(1),
            MetricValue<long>.Available(128 * 1024 * 1024),
            MetricValue<double>.Available(0),
            MetricValue<double>.Available(0)) with
        {
            Gpu = new GpuMetricSet(
                MetricValue<double>.Available(12.5),
                MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable),
                new GpuEngineId(1, 0, 0, "3D"),
                new GpuCollectorDiagnostics
                {
                    ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                    CollectorStatus = MetricAvailability.Partial,
                    Reason = GpuAvailabilityReason.CounterUnavailable,
                    UtilizationCounterStatus = MetricAvailability.Available,
                    DedicatedMemoryCounterStatus = MetricAvailability.Unavailable,
                    SharedMemoryCounterStatus = MetricAvailability.Unavailable
                })
        };

        card.Update(snapshot, []);

        Assert.Equal("12.5%", card.GpuText);
        Assert.Equal("Memory Unavailable", card.GpuStatusText);
        Assert.Contains("GPU 12.5%, Memory Unavailable", card.AutomationName, StringComparison.Ordinal);
        Assert.DoesNotContain("GPU 0", card.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void ExposesQuarantinedGpuCountersWithoutAnnouncingFakeZero()
    {
        const string utilizationDetail =
            "GPU utilization is quarantined until a complete counter gap and reappearance.";
        const string memoryDetail =
            "Dedicated GPU memory is quarantined until a complete counter gap and reappearance.";
        ApplicationCardViewModel card = Card();
        ApplicationMetricSnapshot snapshot = Snapshot(
            MetricValue<double>.Available(1),
            MetricValue<long>.Available(128 * 1024 * 1024),
            MetricValue<double>.Available(0),
            MetricValue<double>.Available(0)) with
        {
            Gpu = new GpuMetricSet(
                MetricValue<double>.Unavailable(
                    MetricAvailability.Unavailable,
                    utilizationDetail),
                MetricValue<ulong>.Unavailable(
                    MetricAvailability.Unavailable,
                    memoryDetail),
                MetricValue<ulong>.Available(1024),
                null,
                new GpuCollectorDiagnostics
                {
                    ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                    CollectorStatus = MetricAvailability.Partial,
                    Reason = GpuAvailabilityReason.AmbiguousCounterLifetime,
                    QuarantinedUtilizationSamples = 1,
                    QuarantinedDedicatedMemorySamples = 1
                })
        };

        card.Update(snapshot, []);

        Assert.Equal("Unavailable", card.GpuText);
        Assert.Contains("quarantined", card.AutomationName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shared", card.AutomationName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GPU 0", card.AutomationName, StringComparison.Ordinal);
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
            "Access denied."),
        Gpu = GpuMetricSet.Unavailable(
            MetricAvailability.AccessDenied,
            GpuAvailabilityReason.AccessDenied,
            "Access denied.")
    };
}
