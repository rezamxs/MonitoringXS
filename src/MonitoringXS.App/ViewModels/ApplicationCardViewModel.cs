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
    public partial string CpuStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string MemoryStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IoText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string IoStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PhysicalDiskText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string PhysicalDiskStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string NetworkStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GpuText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string GpuStatusText { get; set; } = string.Empty;

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
        ScalarPresentation cpu = FormatCpu(snapshot.CpuPercent);
        CpuText = cpu.ValueText;
        CpuStatusText = cpu.StatusText;
        ScalarPresentation memory = FormatBytes(snapshot.WorkingSetBytes);
        MemoryText = memory.ValueText;
        MemoryStatusText = memory.StatusText;
        MetricPairPresentation io = FormatRatePair(
            snapshot.IoReadBytesPerSecond,
            "read",
            snapshot.IoWriteBytesPerSecond,
            "write");
        IoText = io.ValueText;
        IoStatusText = io.StatusText;
        MetricPairPresentation physicalDisk = FormatRatePair(
            snapshot.PhysicalDisk.ReadBytesPerSecond,
            "read",
            snapshot.PhysicalDisk.WriteBytesPerSecond,
            "write");
        PhysicalDiskText = physicalDisk.ValueText;
        PhysicalDiskStatusText = physicalDisk.StatusText;
        MetricPairPresentation network = FormatRatePair(
            snapshot.Network.DownloadBytesPerSecond,
            "receive",
            snapshot.Network.UploadBytesPerSecond,
            "send");
        NetworkText = network.ValueText;
        NetworkStatusText = network.StatusText;
        ScalarPresentation gpu = FormatGpu(snapshot.Gpu.UtilizationPercent);
        GpuText = gpu.ValueText;
        string gpuMemory = FormatGpuMemory(
            snapshot.Gpu.DedicatedMemoryBytes,
            snapshot.Gpu.SharedMemoryBytes);
        bool wholeGpuUnavailable = !snapshot.Gpu.UtilizationPercent.IsAvailable
            && !snapshot.Gpu.DedicatedMemoryBytes.IsAvailable
            && !snapshot.Gpu.SharedMemoryBytes.IsAvailable
            && snapshot.Gpu.UtilizationPercent.Availability
                == snapshot.Gpu.DedicatedMemoryBytes.Availability
            && snapshot.Gpu.UtilizationPercent.Availability
                == snapshot.Gpu.SharedMemoryBytes.Availability;
        GpuStatusText = wholeGpuUnavailable
            ? gpu.StatusText
            : string.Equals(gpu.StatusText, gpuMemory, StringComparison.Ordinal)
            ? gpu.StatusText
            : string.IsNullOrEmpty(gpu.StatusText)
            ? gpuMemory
            : string.IsNullOrEmpty(gpuMemory)
                ? gpu.StatusText
                : $"{gpu.StatusText} · {gpuMemory}";
        ProcessCountText = snapshot.ProcessCount == 1 ? "1 process" : $"{snapshot.ProcessCount} processes";
        bool hasPartialMetric = snapshot.CpuPercent.Availability == MetricAvailability.Partial
            || snapshot.WorkingSetBytes.Availability == MetricAvailability.Partial
            || snapshot.IoReadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.IoWriteBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.PhysicalDisk.ReadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.PhysicalDisk.WriteBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.Network.DownloadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.Network.UploadBytesPerSecond.Availability == MetricAvailability.Partial
            || snapshot.Gpu.UtilizationPercent.Availability == MetricAvailability.Partial
            || snapshot.Gpu.DedicatedMemoryBytes.Availability == MetricAvailability.Partial
            || snapshot.Gpu.SharedMemoryBytes.Availability == MetricAvailability.Partial;
        StatusText = hasPartialMetric
            ? "Running · partial metrics"
            : snapshot.CpuPercent.IsAvailable ? "Running · live" : "Running · metrics warming up";
        string gpuAccessible = wholeGpuUnavailable || string.IsNullOrEmpty(gpuMemory)
            ? gpu.AccessibleText
            : $"{gpu.AccessibleText}, {gpuMemory}";
        string gpuQuarantineDetail = string.Join(
            " ",
            new[]
            {
                snapshot.Gpu.UtilizationPercent.Detail,
                snapshot.Gpu.DedicatedMemoryBytes.Detail,
                snapshot.Gpu.SharedMemoryBytes.Detail
            }
            .Where(detail => !string.IsNullOrWhiteSpace(detail)
                && detail.Contains("quarantin", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(gpuQuarantineDetail))
        {
            gpuAccessible = $"{gpuAccessible}. {gpuQuarantineDetail}";
        }

        AutomationName = $"{DisplayName}. {StatusText}. CPU {cpu.AccessibleText}. Memory {memory.AccessibleText}. Process I/O {io.AccessibleText}. Physical disk {physicalDisk.AccessibleText}. Network {network.AccessibleText}. GPU {gpuAccessible}.";
    }

    private static ScalarPresentation FormatCpu(MetricValue<double> metric)
    {
        if (!metric.IsAvailable)
        {
            return FormatUnavailableScalar(metric);
        }

        string value = $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%";
        return FormatAvailableScalar(value, metric);
    }

    private static ScalarPresentation FormatBytes(MetricValue<long> metric)
    {
        if (!metric.IsAvailable)
        {
            return FormatUnavailableScalar(metric);
        }

        double bytes = metric.Value!.Value;
        string value = PartialPrefix(metric) + (bytes >= 1024d * 1024d * 1024d
            ? $"{(bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture)} MB");
        return FormatAvailableScalar(value, metric);
    }

    private static ScalarPresentation FormatGpu(MetricValue<double> metric)
    {
        if (!metric.IsAvailable)
        {
            return FormatUnavailableScalar(metric);
        }

        string value = $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%";
        return FormatAvailableScalar(value, metric);
    }

    private static string FormatGpuMemory(
        MetricValue<ulong> dedicated,
        MetricValue<ulong> shared)
    {
        if (!dedicated.IsAvailable && !shared.IsAvailable)
        {
            return dedicated.Availability == shared.Availability
                ? $"Memory {FormatAvailability(dedicated)}"
                : $"{FormatAvailability(dedicated)} dedicated, {FormatAvailability(shared)} shared";
        }

        return $"{FormatGpuMemoryValue(dedicated)} dedicated · {FormatGpuMemoryValue(shared)} shared";
    }

    private static string FormatGpuMemoryValue(MetricValue<ulong> metric)
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

    private static ScalarPresentation FormatAvailableScalar<T>(string value, MetricValue<T> metric)
        where T : struct => metric.Availability == MetricAvailability.Partial
        ? new(
            value,
            "Partial · lower bound",
            $"at least {value.TrimStart('\u2265', ' ')}, partial lower bound")
        : new(value, string.Empty, value);

    private static ScalarPresentation FormatUnavailableScalar<T>(MetricValue<T> metric)
        where T : struct => new(
            FormatCompactUnavailable(metric.Availability),
            FormatSupportingAvailability(metric.Availability),
            FormatAvailability(metric));

    private static MetricPairPresentation FormatRatePair(
        MetricValue<double> first,
        string firstDirection,
        MetricValue<double> second,
        string secondDirection)
    {
        if (!first.IsAvailable
            && !second.IsAvailable
            && first.Availability == second.Availability)
        {
            return new(
                FormatCompactUnavailable(first.Availability),
                FormatSupportingAvailability(first.Availability),
                FormatAvailability(first));
        }

        string values = $"{FormatDirectionalRate(first, firstDirection)} · {FormatDirectionalRate(second, secondDirection)}";
        string accessibleValues =
            $"{FormatAccessibleDirectionalRate(first, firstDirection)}, {FormatAccessibleDirectionalRate(second, secondDirection)}";
        string[] statuses =
        [
            FormatMetricStatus(first),
            FormatMetricStatus(second)
        ];
        string status = string.Join(
            " · ",
            statuses.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal));
        string accessibleStatus = string.Join(
            ", ",
            statuses
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value.Replace(" · ", " ", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal));
        return new(
            values,
            status,
            string.IsNullOrEmpty(accessibleStatus)
                ? accessibleValues
                : $"{accessibleValues}, {accessibleStatus}");
    }

    private static string FormatDirectionalRate(MetricValue<double> metric, string direction) =>
        metric.IsAvailable
            ? $"{FormatRate(metric)} {direction}"
            : $"{FormatCompactUnavailable(metric.Availability)} {direction}";

    private static string FormatAccessibleDirectionalRate(MetricValue<double> metric, string direction)
    {
        if (!metric.IsAvailable)
        {
            return $"{FormatCompactUnavailable(metric.Availability)} {direction}";
        }

        string rate = FormatRate(metric);
        return metric.IsComplete
            ? $"{rate} {direction}"
            : $"at least {rate.TrimStart('\u2265', ' ')} {direction}";
    }

    private static string FormatMetricStatus<T>(MetricValue<T> metric)
        where T : struct => metric.Availability == MetricAvailability.Partial
        ? "Partial · lower bound"
        : FormatSupportingAvailability(metric.Availability);

    private static string FormatCompactUnavailable(MetricAvailability availability) =>
        availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";

    private static string FormatSupportingAvailability(MetricAvailability availability) => availability switch
    {
        MetricAvailability.AccessDenied => "Access denied",
        MetricAvailability.Unsupported => "Unsupported",
        MetricAvailability.Error => "Error",
        _ => string.Empty
    };

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

    private readonly record struct ScalarPresentation(
        string ValueText,
        string StatusText,
        string AccessibleText);

    private readonly record struct MetricPairPresentation(
        string ValueText,
        string StatusText,
        string AccessibleText);
}
