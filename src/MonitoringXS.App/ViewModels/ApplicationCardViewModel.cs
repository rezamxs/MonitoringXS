using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class ApplicationCardViewModel : ObservableObject, IApplicationListItemViewModel
{
    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Publisher { get; set; } = "Publisher unavailable";

    [ObservableProperty]
    public partial string CpuText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string MemoryText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string IoText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string PhysicalDiskText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string NetworkText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string NetworkStatusText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string ProcessCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Running";

    [ObservableProperty]
    public partial string AutomationName { get; set; } = "Running application";

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
        PhysicalDiskText = $"{FormatRate(snapshot.PhysicalDisk.ReadBytesPerSecond)} read / {FormatRate(snapshot.PhysicalDisk.WriteBytesPerSecond)} write";
        NetworkText = $"{FormatRate(snapshot.Network.DownloadBytesPerSecond)} down / {FormatRate(snapshot.Network.UploadBytesPerSecond)} up";
        NetworkStatusText = FormatAvailability(snapshot.Network.DownloadBytesPerSecond);
        ProcessCountText = snapshot.ProcessCount == 1 ? "1 process" : $"{snapshot.ProcessCount} processes";
        bool hasPartialMetric = snapshot.CpuPercent.Availability == MetricAvailability.Partial
            || snapshot.WorkingSetBytes.Availability == MetricAvailability.Partial
            || snapshot.IoReadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.IoWriteBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.PhysicalDisk.ReadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.PhysicalDisk.WriteBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.Network.DownloadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.Network.UploadBytesPerSecond.Availability == MetricAvailability.Partial;
        StatusText = hasPartialMetric
            ? "Running · partial metrics"
            : snapshot.CpuPercent.IsAvailable ? "Running · live" : "Running · metrics warming up";
        string physicalDiskAccessibleText = FormatAccessibleMetricPair(
            PhysicalDiskText,
            snapshot.PhysicalDisk.ReadBytesPerSecond);
        string networkAccessibleText = FormatAccessibleMetricPair(
            NetworkText,
            snapshot.Network.DownloadBytesPerSecond);
        AutomationName = $"{DisplayName}. {StatusText}. CPU {CpuText}. Memory {MemoryText}. Process I/O {IoText}. Physical disk {physicalDiskAccessibleText}. Network {networkAccessibleText}.";
    }

    private static string FormatCpu(MetricValue<double> metric) => metric.IsAvailable
        ? $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
        : metric.Availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";

    private static string FormatBytes(MetricValue<long> metric)
    {
        if (!metric.IsAvailable)
        {
            return "Unavailable";
        }

        double bytes = metric.Value!.Value;
        return PartialPrefix(metric) + (bytes >= 1024d * 1024d * 1024d
            ? $"{(bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture)} MB");
    }

    private static string FormatRate(MetricValue<double> metric)
    {
        if (!metric.IsAvailable)
        {
            return metric.Availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";
        }

        double bytesPerSecond = metric.Value!.Value;
        string value = bytesPerSecond >= 1024d * 1024d
            ? $"{(bytesPerSecond / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture)} MB/s"
            : bytesPerSecond >= 1024d
                ? $"{(bytesPerSecond / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB/s"
                : $"{bytesPerSecond.ToString("0", CultureInfo.InvariantCulture)} B/s";
        return PartialPrefix(metric) + value;
    }

    private static string PartialPrefix<T>(MetricValue<T> metric) where T : struct =>
        metric.IsComplete ? string.Empty : "≥ ";

    private static string FormatAccessibleMetricPair(string values, MetricValue<double> availability) =>
        availability.Availability switch
        {
            MetricAvailability.Available => values,
            MetricAvailability.Partial => $"{values}, partial lower bound",
            _ => FormatAvailability(availability)
        };

    private static string FormatAvailability<T>(MetricValue<T> metric)
        where T : struct => metric.Availability switch
        {
            MetricAvailability.Available => "Available",
            MetricAvailability.Partial => "Partial (lower bound)",
            MetricAvailability.WarmingUp => "Warming up",
            MetricAvailability.AccessDenied => "Access denied",
            MetricAvailability.Unsupported => "Unsupported",
            MetricAvailability.Unavailable => "Unavailable",
            _ => "Error"
        };
}
