using System.Runtime.CompilerServices;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MonitoringXS.App.Tests;

public sealed class ApplicationTabViewModelTests
{
    [Fact]
    public void RealAndFallbackIconStatesAreMutuallyExclusive()
    {
        ApplicationCardViewModel card = new()
        {
            LogicalApplicationId = "sample-app",
            Disposition = ApplicationDisposition.Installed
        };
        ApplicationTabViewModel tab = new("sample-app", "Sample App");

        Assert.False(card.HasAppIcon);
        Assert.True(card.HasFallbackIcon);
        Assert.False(tab.HasAppIcon);
        Assert.True(tab.HasFallbackIcon);

        BitmapImage icon = (BitmapImage)RuntimeHelpers.GetUninitializedObject(typeof(BitmapImage));
        card.AppIconSource = icon;
        tab.AppIconSource = icon;

        Assert.True(card.HasAppIcon);
        Assert.False(card.HasFallbackIcon);
        Assert.True(tab.HasAppIcon);
        Assert.False(tab.HasFallbackIcon);
    }

    [Fact]
    public void CardAndTabPreferTheExecutableAndDoNotResolveAgainOnRefresh()
    {
        RecordingIconProvider provider = new();
        ApplicationMetricSnapshot snapshot = Snapshot() with
        {
            Processes =
            [
                new(
                    new ProcessInstanceId(42, DateTimeOffset.UtcNow),
                    "sample.exe",
                    @"C:\Apps\Sample\sample.exe",
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    true)
            ]
        };
        ApplicationCardViewModel card = new(iconProvider: provider)
        {
            LogicalApplicationId = "sample-app",
            Disposition = ApplicationDisposition.Installed
        };
        ApplicationTabViewModel tab = new("sample-app", "Sample App", iconProvider: provider);

        card.Update(snapshot, []);
        tab.Update(snapshot, []);
        card.Update(snapshot, []);
        tab.Update(snapshot, []);

        Assert.Equal(2, provider.Paths.Count);
        Assert.All(provider.Paths, path => Assert.Equal(@"C:\Apps\Sample\sample.exe", path));
    }

    [Fact]
    public void CpuHistoryCollectionStaysStableBoundedAndPreservesGaps()
    {
        DateTimeOffset start = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        ApplicationTabViewModel tab = new("sample-app", "Sample App");
        var samples = tab.CpuSamples;
        ApplicationHistoryPoint[] first = Enumerable.Range(0, 60)
            .Select(index => new ApplicationHistoryPoint(
                start.AddSeconds(index),
                index == 30 ? null : index,
                null))
            .ToArray();

        tab.Update(Snapshot(), first);
        tab.Update(Snapshot(), first.Append(new(start.AddSeconds(60), 60, null)).ToArray());

        Assert.Same(samples, tab.CpuSamples);
        Assert.Equal(60, samples.Count);
        Assert.Equal(start.AddSeconds(1), samples[0].Timestamp);
        Assert.Null(samples[29].Value);
        Assert.Equal(60, samples[^1].Value);
    }

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

    private sealed class RecordingIconProvider : IApplicationIconProvider
    {
        public List<string> Paths { get; } = [];

        public ValueTask<ApplicationIconData?> GetIconAsync(
            string sourcePath,
            int pixelSize,
            CancellationToken cancellationToken)
        {
            Paths.Add(sourcePath);
            return ValueTask.FromResult<ApplicationIconData?>(null);
        }
    }
}
