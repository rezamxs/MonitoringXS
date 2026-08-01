using System.Globalization;
using System.Text;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

internal static class ProcessDetailsTextFormatter
{
    public static string Format(
        ApplicationMetricSnapshot snapshot,
        ProcessDescriptor process,
        DateTimeOffset timestamp)
    {
        StringBuilder text = new();
        Append(text, "Monitoring XS process details");
        Append(text, "Display name", snapshot.Application.DisplayName);
        Append(text, "Process name", process.NormalizedProcessName);
        Append(text, "PID", process.InstanceId.ProcessId.ToString(CultureInfo.InvariantCulture));
        Append(text, "Start time (UTC)", process.InstanceId.StartTimeUtc.ToString("O", CultureInfo.InvariantCulture));
        Append(text, "Executable path", process.ExecutablePath ?? "Unavailable");
        Append(text, "Current status", "Running");
        Append(text, "CPU (application)", Percent(snapshot.CpuPercent));
        Append(text, "Memory (application)", Bytes(snapshot.WorkingSetBytes));
        Append(
            text,
            "Process I/O (application)",
            $"read {Rate(snapshot.IoReadBytesPerSecond)}; write {Rate(snapshot.IoWriteBytesPerSecond)}");
        Append(
            text,
            "Physical Disk (application)",
            $"read {Rate(snapshot.PhysicalDisk.ReadBytesPerSecond)}; write {Rate(snapshot.PhysicalDisk.WriteBytesPerSecond)}");
        Append(
            text,
            "Network (application)",
            $"receive {Rate(snapshot.Network.DownloadBytesPerSecond)}; send {Rate(snapshot.Network.UploadBytesPerSecond)}");
        Append(text, "GPU (application)", Percent(snapshot.Gpu.UtilizationPercent));
        Append(text, "Metric availability", AvailabilitySummary(snapshot));
        Append(text, "Monitoring XS timestamp (UTC)", timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private static void Append(StringBuilder text, string label, string? value = null)
    {
        if (value is null)
        {
            text.AppendLine(label);
            return;
        }

        text.Append(label).Append(": ").AppendLine(value);
    }

    private static string Percent(MetricValue<double> metric) =>
        metric.IsAvailable
            ? $"{Partial(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}% ({metric.Availability})"
            : $"Unavailable ({metric.Availability})";

    private static string Bytes(MetricValue<long> metric) =>
        metric.IsAvailable
            ? $"{Partial(metric)}{FormatBytes(metric.Value!.Value)} ({metric.Availability})"
            : $"Unavailable ({metric.Availability})";

    private static string Rate(MetricValue<double> metric) =>
        metric.IsAvailable
            ? $"{Partial(metric)}{FormatBytes(metric.Value!.Value)}/s ({metric.Availability})"
            : $"Unavailable ({metric.Availability})";

    private static string FormatBytes(double bytes) => bytes >= 1024d * 1024d * 1024d
        ? $"{(bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture)} GB"
        : bytes >= 1024d * 1024d
            ? $"{(bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture)} MB"
            : bytes >= 1024d
                ? $"{(bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB"
                : $"{bytes.ToString("0", CultureInfo.InvariantCulture)} B";

    private static string AvailabilitySummary(ApplicationMetricSnapshot snapshot) =>
        $"CPU {snapshot.CpuPercent.Availability}; "
        + $"memory {snapshot.WorkingSetBytes.Availability}; "
        + $"Process I/O {Pair(snapshot.IoReadBytesPerSecond, snapshot.IoWriteBytesPerSecond)}; "
        + $"Physical Disk {Pair(snapshot.PhysicalDisk.ReadBytesPerSecond, snapshot.PhysicalDisk.WriteBytesPerSecond)}; "
        + $"Network {Pair(snapshot.Network.DownloadBytesPerSecond, snapshot.Network.UploadBytesPerSecond)}; "
        + $"GPU {snapshot.Gpu.UtilizationPercent.Availability}";

    private static string Pair<T>(MetricValue<T> first, MetricValue<T> second)
        where T : struct => first.Availability == second.Availability
        ? first.Availability.ToString()
        : $"{first.Availability}/{second.Availability}";

    private static string Partial<T>(MetricValue<T> metric)
        where T : struct => metric.IsComplete ? string.Empty : "at least ";
}
