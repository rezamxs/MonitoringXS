using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class ApplicationTabViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string CpuText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string MemoryText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string IoReadText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string IoWriteText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string PhysicalDiskReadText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string PhysicalDiskWriteText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string PhysicalDiskStatusText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string PhysicalDiskTotalsText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string PhysicalDiskOperationsText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string PhysicalDiskDiagnosticsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkDownloadText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string NetworkUploadText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string NetworkStatusText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string NetworkTotalsText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string NetworkEndpointsText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string NetworkDiagnosticsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProcessSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClassificationReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClassificationConfidence { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IList<double?> CpuSamples { get; set; } = Array.Empty<double?>();

    public ApplicationTabViewModel(string logicalApplicationId, string title)
    {
        LogicalApplicationId = logicalApplicationId;
        Title = title;
    }

    public string LogicalApplicationId { get; }

    public void Update(ApplicationMetricSnapshot snapshot, IReadOnlyList<ApplicationHistoryPoint> history)
    {
        Title = snapshot.Application.DisplayName;
        CpuText = snapshot.CpuPercent.IsAvailable
            ? $"{PartialPrefix(snapshot.CpuPercent)}{snapshot.CpuPercent.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
            : snapshot.CpuPercent.Availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";
        MemoryText = FormatMemory(snapshot.WorkingSetBytes);
        IoReadText = FormatRate(snapshot.IoReadBytesPerSecond);
        IoWriteText = FormatRate(snapshot.IoWriteBytesPerSecond);
        PhysicalDiskReadText = FormatRate(snapshot.PhysicalDisk.ReadBytesPerSecond);
        PhysicalDiskWriteText = FormatRate(snapshot.PhysicalDisk.WriteBytesPerSecond);
        PhysicalDiskStatusText = FormatAvailability(snapshot.PhysicalDisk.ReadBytesPerSecond);
        PhysicalDiskTotalsText = $"Read {FormatBytes(snapshot.PhysicalDisk.SessionReadBytes)} / Write {FormatBytes(snapshot.PhysicalDisk.SessionWriteBytes)}";
        PhysicalDiskOperationsText = $"Read {FormatCount(snapshot.PhysicalDisk.SessionReadOperationCount)} / Write {FormatCount(snapshot.PhysicalDisk.SessionWriteOperationCount)}";
        PhysicalDiskCollectorDiagnostics diagnostics = snapshot.PhysicalDisk.Diagnostics;
        PhysicalDiskDiagnosticsText = $"Events {diagnostics.EventsObserved}; rate {diagnostics.EventRatePerSecond.ToString("0", CultureInfo.InvariantCulture)}/s; queue {diagnostics.CurrentQueueDepth}/{diagnostics.MaximumQueueDepth} max; dropped {diagnostics.QueueEventsDropped}; ETW lost {diagnostics.EtwEventsLost}; buffer {diagnostics.EtwBufferSizeMegabytes} MB; unattributed {diagnostics.UnattributedEvents}; PID-reuse rejected {diagnostics.PidReuseEventsRejected}.";
        NetworkDownloadText = FormatRate(snapshot.Network.DownloadBytesPerSecond);
        NetworkUploadText = FormatRate(snapshot.Network.UploadBytesPerSecond);
        NetworkStatusText = $"{FormatAvailability(snapshot.Network.DownloadBytesPerSecond)}; reason {snapshot.Network.Reason}.";
        NetworkTotalsText = $"Downloaded {FormatBytes(snapshot.Network.SessionDownloadedBytes)} / Uploaded {FormatBytes(snapshot.Network.SessionUploadedBytes)}";
        NetworkEndpointsText = $"TCP connections {FormatCount(snapshot.Network.ActiveTcpConnectionCount)} / UDP endpoints {FormatCount(snapshot.Network.UdpEndpointCount)}";
        NetworkCollectorDiagnostics networkDiagnostics = snapshot.Network.Diagnostics;
        NetworkDiagnosticsText = $"Events {networkDiagnostics.EventsObserved}; rate {networkDiagnostics.EventRatePerSecond.ToString("0", CultureInfo.InvariantCulture)}/s; queue {networkDiagnostics.CurrentQueueDepth}/{networkDiagnostics.MaximumQueueDepth} max; dropped {networkDiagnostics.QueueEventsDropped}; ETW lost {networkDiagnostics.EtwEventsLost}; unattributed {networkDiagnostics.UnattributedEvents}; PID-reuse rejected {networkDiagnostics.PidReuseEventsRejected}; completeness {(networkDiagnostics.SessionTotalsAreLowerBounds ? "lower bound" : "complete")}.";
        ProcessSummary = $"{snapshot.ProcessCount} process{(snapshot.ProcessCount == 1 ? string.Empty : "es")} · {snapshot.Application.Disposition}";
        ClassificationReason = snapshot.Application.ClassificationReason;
        ClassificationConfidence = $"{snapshot.Application.Confidence} confidence";
        CpuSamples = history.Select(point => point.CpuPercent).ToArray();
    }

    private static string FormatMemory(MetricValue<long> metric) => metric.IsAvailable
        ? $"{PartialPrefix(metric)}{(metric.Value!.Value / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture)} MB"
        : "Unavailable";

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

    private static string FormatBytes(MetricValue<ulong> metric)
    {
        if (!metric.IsAvailable)
        {
            return FormatAvailability(metric);
        }

        double bytes = metric.Value!.Value;
        string value = bytes >= 1024d * 1024d * 1024d
            ? $"{(bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture)} GB"
            : bytes >= 1024d * 1024d
                ? $"{(bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture)} MB"
                : bytes >= 1024d
                    ? $"{(bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB"
                    : $"{bytes.ToString("0", CultureInfo.InvariantCulture)} B";
        return PartialPrefix(metric) + value;
    }

    private static string FormatCount(MetricValue<ulong> metric) => metric.IsAvailable
        ? PartialPrefix(metric) + metric.Value!.Value.ToString("N0", CultureInfo.InvariantCulture)
        : FormatAvailability(metric);

    private static string FormatCount(MetricValue<int> metric) => metric.IsAvailable
        ? PartialPrefix(metric) + metric.Value!.Value.ToString("N0", CultureInfo.InvariantCulture)
        : FormatAvailability(metric);

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

    private static string PartialPrefix<T>(MetricValue<T> metric) where T : struct =>
        metric.IsComplete ? string.Empty : "≥ ";
}
