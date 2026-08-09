using Microsoft.UI.Xaml;
using MonitoringXS.App.ViewModels;
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
