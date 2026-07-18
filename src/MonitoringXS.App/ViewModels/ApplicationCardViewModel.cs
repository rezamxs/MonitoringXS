using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class ApplicationCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _publisher = "Publisher unavailable";

    [ObservableProperty]
    private string _cpuText = "Warming up";

    [ObservableProperty]
    private string _memoryText = "Unavailable";

    [ObservableProperty]
    private string _ioText = "Unavailable";

    [ObservableProperty]
    private string _processCountText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Running";

    public required string LogicalApplicationId { get; init; }

    public required ApplicationDisposition Disposition { get; init; }

    public ApplicationMetricSnapshot? LatestSnapshot { get; private set; }

    public IReadOnlyList<ApplicationHistoryPoint> History { get; private set; } = [];

    public void Update(ApplicationMetricSnapshot snapshot, IReadOnlyList<ApplicationHistoryPoint> history)
    {
        LatestSnapshot = snapshot;
        History = history;
        DisplayName = snapshot.Application.DisplayName;
        Publisher = snapshot.Application.Publisher ?? "Publisher unavailable";
        CpuText = FormatCpu(snapshot.CpuPercent);
        MemoryText = FormatBytes(snapshot.WorkingSetBytes);
        IoText = $"{FormatRate(snapshot.IoReadBytesPerSecond)} read · {FormatRate(snapshot.IoWriteBytesPerSecond)} write";
        ProcessCountText = snapshot.ProcessCount == 1 ? "1 process" : $"{snapshot.ProcessCount} processes";
        bool hasPartialMetric = snapshot.CpuPercent.Availability == MetricAvailability.Partial
            || snapshot.WorkingSetBytes.Availability == MetricAvailability.Partial
            || snapshot.IoReadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.IoWriteBytesPerSecond.Availability == MetricAvailability.Partial;
        StatusText = hasPartialMetric
            ? "Running · partial metrics"
            : snapshot.CpuPercent.IsAvailable ? "Running · live" : "Running · metrics warming up";
    }

    private static string FormatCpu(MetricValue<double> metric) => metric.IsAvailable
        ? $"{PartialPrefix(metric)}{metric.Value:0.0}%"
        : metric.Availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";

    private static string FormatBytes(MetricValue<long> metric)
    {
        if (!metric.IsAvailable)
        {
            return "Unavailable";
        }

        double bytes = metric.Value!.Value;
        return PartialPrefix(metric) + (bytes >= 1024d * 1024d * 1024d
            ? $"{bytes / (1024d * 1024d * 1024d):0.00} GB"
            : $"{bytes / (1024d * 1024d):0} MB");
    }

    private static string FormatRate(MetricValue<double> metric)
    {
        if (!metric.IsAvailable)
        {
            return metric.Availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";
        }

        double bytesPerSecond = metric.Value!.Value;
        string value = bytesPerSecond >= 1024d * 1024d
            ? $"{bytesPerSecond / (1024d * 1024d):0.0} MB/s"
            : bytesPerSecond >= 1024d
                ? $"{bytesPerSecond / 1024d:0.0} KB/s"
                : $"{bytesPerSecond:0} B/s";
        return PartialPrefix(metric) + value;
    }

    private static string PartialPrefix<T>(MetricValue<T> metric) where T : struct =>
        metric.IsComplete ? string.Empty : "≥ ";
}
