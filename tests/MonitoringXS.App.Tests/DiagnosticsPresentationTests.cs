using Microsoft.UI.Xaml;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class DiagnosticsPresentationTests
{
    [Fact]
    public void HealthyAndDegradedCollectorStatesRemainTruthful()
    {
        Assert.Equal(
            MetricAvailability.Available,
            DiagnosticsPageViewModel.Aggregate(
                [MetricAvailability.Available, MetricAvailability.Available]));
        Assert.Equal(
            MetricAvailability.Partial,
            DiagnosticsPageViewModel.Aggregate(
                [MetricAvailability.Available, MetricAvailability.AccessDenied]));
        Assert.Equal(
            MetricAvailability.Unsupported,
            DiagnosticsPageViewModel.Aggregate([MetricAvailability.Unsupported]));
        Assert.Equal(
            MetricAvailability.Error,
            DiagnosticsPageViewModel.Aggregate(
                [MetricAvailability.WarmingUp, MetricAvailability.Error]));
    }

    [Fact]
    public void SafeCopyExcludesSensitivePathsAndCommandLineData()
    {
        DiagnosticItem safe = new("GPU", "Available");
        DiagnosticItem path = new(
            "Database path",
            @"C:\Users\Private\history.db",
            IsTechnicalValue: true,
            IncludeInSafeSummary: false);

        string result = DiagnosticsPageViewModel.BuildSafeSummary(
            "Monitoring XS safe diagnostics",
            "Healthy",
            [safe, path]);

        Assert.Contains("GPU: Available", result, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", result, StringComparison.Ordinal);
        Assert.DoesNotContain("command line", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TechnicalValuesUseExplicitLeftToRightPresentation()
    {
        DiagnosticItem item = new("Path", @"C:\Data\history.db", IsTechnicalValue: true)
        {
            ValueFlowDirection = FlowDirection.LeftToRight
        };

        Assert.Equal(FlowDirection.LeftToRight, item.ValueFlowDirection);
    }

    [Fact]
    public void SelfProcessSamplerReportsWorkingSetAndEventuallyCpu()
    {
        SelfProcessSampler sampler = new();
        double first = sampler.Sample();
        Assert.Equal(-1, first);
        Assert.True(sampler.WorkingSetBytes > 0);

        double second = sampler.Sample();
        Assert.InRange(second, 0, 1000);
        Assert.True(sampler.WorkingSetBytes > 0);
    }

    [Fact]
    public void MetricMetadataDistinguishesScopesAndSamplingKinds()
    {
        MetricMetadata cpu = SystemOverviewService.CpuMetadata;
        Assert.Equal(MetricUnit.Percent, cpu.Unit);
        Assert.Equal(MetricScope.System, cpu.Scope);
        Assert.Equal(MetricSource.WindowsPerformanceCounters, cpu.Source);
        Assert.Equal(MetricSamplingKind.CurrentSnapshot, cpu.Sampling);

        MetricMetadata disk = SystemOverviewService.DiskMetadata;
        Assert.Equal(MetricUnit.BytesPerSecond, disk.Unit);
        Assert.Equal(MetricSource.AdvancedMonitoringEngine, disk.Source);
        Assert.Equal(MetricSamplingKind.AggregatedOverInterval, disk.Sampling);

        Assert.Equal(MetricUnit.Unknown, MetricMetadata.Unknown.Unit);
        Assert.Equal(MetricScope.Collector, MetricMetadata.Unknown.Scope);
        Assert.Equal(MetricSource.Unknown, MetricMetadata.Unknown.Source);
        Assert.Equal(MetricSamplingKind.Unknown, MetricMetadata.Unknown.Sampling);
    }

    [Fact]
    public void DiagnosticsPageIsScrollableKeyboardAccessibleAndNonDestructive()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "DiagnosticsPage.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "src", "MonitoringXS.App", "DiagnosticsPage.xaml.cs"));

        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UseSystemFocusVisuals=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ValueFlowDirection", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshCommand.Cancel()", code, StringComparison.Ordinal);
        Assert.Contains("CopySafeSummaryCommand.Cancel()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", code, StringComparison.Ordinal);
        Assert.DoesNotContain("restart", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TickRowsRenderMeasuredDurationsAndUnavailableForUnmeasuredStages()
    {
        LocalizationService localization = new();
        CaptureTickTimings partial = new()
        {
            Total = TimeSpan.FromMilliseconds(12.5),
            Discovery = TimeSpan.FromMilliseconds(3),
            Aggregation = null
        };

        IReadOnlyList<DiagnosticItem> rows = DiagnosticsPageViewModel.BuildTickRows(partial, localization);

        Assert.Equal(13, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsTechnicalValue));
        Assert.Equal(FlowDirection.LeftToRight, rows[0].ValueFlowDirection);
        Assert.Equal("12.5 ms", rows[0].Value);
        Assert.Equal("3 ms", rows[2].Value);
        Assert.Equal(localization.Get(LocalizationKeys.Unavailable), rows[6].Value);

        IReadOnlyList<DiagnosticItem> unmeasured = DiagnosticsPageViewModel.BuildTickRows(null, localization);
        Assert.Equal(13, unmeasured.Count);
        Assert.All(unmeasured, row =>
            Assert.Equal(localization.Get(LocalizationKeys.Unavailable), row.Value));
    }

    [Fact]
    public void HistoryWriteRowRendersMicrosecondsAndUnavailableWithoutMeasurements()
    {
        LocalizationService localization = new();

        DiagnosticItem measured = DiagnosticsPageViewModel.BuildHistoryWriteRow(1500, localization);
        DiagnosticItem unmeasured = DiagnosticsPageViewModel.BuildHistoryWriteRow(null, localization);

        Assert.Equal(localization.Get("DiagnosticsTickHistoryWrite"), measured.Label);
        Assert.Equal("1.5 ms", measured.Value);
        Assert.True(measured.IsTechnicalValue);
        Assert.Equal(localization.Get(LocalizationKeys.Unavailable), unmeasured.Value);
    }

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
