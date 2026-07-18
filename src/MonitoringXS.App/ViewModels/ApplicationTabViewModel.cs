using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class ApplicationTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _cpuText = "Warming up";

    [ObservableProperty]
    private string _memoryText = "Unavailable";

    [ObservableProperty]
    private string _ioReadText = "Unavailable";

    [ObservableProperty]
    private string _ioWriteText = "Unavailable";

    [ObservableProperty]
    private string _processSummary = string.Empty;

    [ObservableProperty]
    private string _classificationReason = string.Empty;

    public ApplicationTabViewModel(string logicalApplicationId, string title)
    {
        LogicalApplicationId = logicalApplicationId;
        _title = title;
    }

    public string LogicalApplicationId { get; }

    public ObservableCollection<double?> CpuSamples { get; } = [];

    public void Update(ApplicationMetricSnapshot snapshot, IReadOnlyList<ApplicationHistoryPoint> history)
    {
        Title = snapshot.Application.DisplayName;
        CpuText = snapshot.CpuPercent.IsAvailable
            ? $"{PartialPrefix(snapshot.CpuPercent)}{snapshot.CpuPercent.Value:0.0}%"
            : snapshot.CpuPercent.Availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";
        MemoryText = FormatMemory(snapshot.WorkingSetBytes);
        IoReadText = FormatRate(snapshot.IoReadBytesPerSecond);
        IoWriteText = FormatRate(snapshot.IoWriteBytesPerSecond);
        ProcessSummary = $"{snapshot.ProcessCount} process{(snapshot.ProcessCount == 1 ? string.Empty : "es")} · {snapshot.Application.Disposition}";
        ClassificationReason = snapshot.Application.ClassificationReason;

        CpuSamples.Clear();
        foreach (ApplicationHistoryPoint point in history)
        {
            CpuSamples.Add(point.CpuPercent);
        }
    }

    private static string FormatMemory(MetricValue<long> metric) => metric.IsAvailable
        ? $"{PartialPrefix(metric)}{metric.Value!.Value / (1024d * 1024d):0} MB"
        : "Unavailable";

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
