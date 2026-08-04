using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.App.Localization;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class ApplicationCardViewModel : ObservableObject, IApplicationListItemViewModel
{
    private readonly LocalizationService _localization;

    public ApplicationCardViewModel(LocalizationService? localization = null)
    {
        _localization = localization ?? new LocalizationService();
    }
    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Publisher { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CpuText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CpuStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IoText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IoStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PhysicalDiskText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PhysicalDiskStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GpuText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GpuStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProcessCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutomationName { get; set; } = string.Empty;

    public required string LogicalApplicationId { get; init; }

    public required ApplicationDisposition Disposition { get; init; }

    public ApplicationMetricSnapshot? LatestSnapshot { get; private set; }

    public IReadOnlyList<ApplicationHistoryPoint> History { get; private set; } = [];

    public void Update(ApplicationMetricSnapshot snapshot, IReadOnlyList<ApplicationHistoryPoint> history)
    {
        LatestSnapshot = snapshot;
        History = history;
        DisplayName = snapshot.Application.DisplayName;
        Publisher = snapshot.Application.Publisher ?? _localization.Get(LocalizationKeys.PublisherUnavailable);
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
            "write",
            HasNoAttributedActivity(
                snapshot.PhysicalDisk.SessionReadBytes,
                snapshot.PhysicalDisk.SessionWriteBytes));
        PhysicalDiskText = physicalDisk.ValueText;
        PhysicalDiskStatusText = physicalDisk.StatusText;
        MetricPairPresentation network = FormatRatePair(
            snapshot.Network.DownloadBytesPerSecond,
            "receive",
            snapshot.Network.UploadBytesPerSecond,
            "send",
            HasNoAttributedActivity(
                snapshot.Network.SessionDownloadedBytes,
                snapshot.Network.SessionUploadedBytes));
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
        ProcessCountText = snapshot.ProcessCount == 1
            ? _localization.Format(LocalizationKeys.ProcessCountSingular, snapshot.ProcessCount)
            : _localization.Format(LocalizationKeys.ProcessCountPlural, snapshot.ProcessCount);
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
            ? _localization.Get(LocalizationKeys.RunningPartial)
            : snapshot.CpuPercent.IsAvailable
                ? _localization.Get(LocalizationKeys.RunningLive)
                : _localization.Get(LocalizationKeys.RunningWarming);
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

    public void Relocalize()
    {
        if (LatestSnapshot is not null)
        {
            Update(LatestSnapshot, History);
        }
    }

    private ScalarPresentation FormatCpu(MetricValue<double> metric)
    {
        if (!metric.IsAvailable)
        {
            return FormatUnavailableScalar(metric);
        }

        string value = $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%";
        return FormatAvailableScalar(value, metric);
    }

    private ScalarPresentation FormatBytes(MetricValue<long> metric)
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

    private ScalarPresentation FormatGpu(MetricValue<double> metric)
    {
        if (!metric.IsAvailable)
        {
            return FormatUnavailableScalar(metric);
        }

        string value = $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%";
        return FormatAvailableScalar(value, metric);
    }

    private string FormatGpuMemory(
        MetricValue<ulong> dedicated,
        MetricValue<ulong> shared)
    {
        if (!dedicated.IsAvailable && !shared.IsAvailable)
        {
            return dedicated.Availability == shared.Availability
                ? $"{_localization.Get(LocalizationKeys.MemoryLabel)} {FormatAvailability(dedicated)}"
                : $"{FormatAvailability(dedicated)} {_localization.Get(LocalizationKeys.DedicatedLabel)}, {FormatAvailability(shared)} {_localization.Get(LocalizationKeys.SharedLabel)}";
        }

        return $"{FormatGpuMemoryValue(dedicated)} {_localization.Get(LocalizationKeys.DedicatedLabel)} · {FormatGpuMemoryValue(shared)} {_localization.Get(LocalizationKeys.SharedLabel)}";
    }

    private string FormatGpuMemoryValue(MetricValue<ulong> metric)
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

    private ScalarPresentation FormatAvailableScalar<T>(string value, MetricValue<T> metric)
        where T : struct => metric.Availability == MetricAvailability.Partial
        ? new(
            value,
            _localization.Get(LocalizationKeys.PartialLowerBound),
            $"{_localization.Get(LocalizationKeys.AtLeast)} {value.TrimStart('\u2265', ' ')}, partial lower bound")
        : new(value, string.Empty, value);

    private ScalarPresentation FormatUnavailableScalar<T>(MetricValue<T> metric)
        where T : struct => new(
            FormatCompactUnavailable(metric.Availability),
            FormatSupportingAvailability(metric.Availability, metric.Detail),
            FormatAvailability(metric));

    private MetricPairPresentation FormatRatePair(
        MetricValue<double> first,
        string firstDirection,
        MetricValue<double> second,
        string secondDirection,
        bool noAttributedActivityYet = false)
    {
        if (!first.IsAvailable
            && !second.IsAvailable
            && first.Availability == second.Availability)
        {
            return new(
                FormatCompactUnavailable(first.Availability),
                FormatSupportingAvailability(first.Availability, first.Detail ?? second.Detail),
                FormatAvailability(first));
        }

        string values = $"{FormatDirectionalRate(first, firstDirection)} · {FormatDirectionalRate(second, secondDirection)}";
        string accessibleValues =
            $"{FormatAccessibleDirectionalRate(first, firstDirection)}, {FormatAccessibleDirectionalRate(second, secondDirection)}";
        string[] statuses =
        [
            FormatMetricStatus(first),
            FormatMetricStatus(second),
            noAttributedActivityYet ? _localization.Get(LocalizationKeys.NoAttributedActivity) : string.Empty
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

    private string FormatDirectionalRate(MetricValue<double> metric, string direction) =>
        metric.IsAvailable
            ? $"{FormatRate(metric)} {direction}"
            : $"{FormatCompactUnavailable(metric.Availability)} {direction}";

    private string FormatAccessibleDirectionalRate(MetricValue<double> metric, string direction)
    {
        if (!metric.IsAvailable)
        {
            return $"{FormatCompactUnavailable(metric.Availability)} {direction}";
        }

        string rate = FormatRate(metric);
        return metric.IsComplete
            ? $"{rate} {direction}"
            : $"{_localization.Get(LocalizationKeys.AtLeast)} {rate.TrimStart('\u2265', ' ')} {direction}";
    }

    private string FormatMetricStatus<T>(MetricValue<T> metric)
        where T : struct => metric.Availability == MetricAvailability.Partial
        ? _localization.Get(LocalizationKeys.PartialLowerBound)
        : FormatSupportingAvailability(metric.Availability, metric.Detail);

    private string FormatCompactUnavailable(MetricAvailability availability) =>
        availability == MetricAvailability.WarmingUp
            ? _localization.Get(LocalizationKeys.WarmingUp)
            : _localization.Get(LocalizationKeys.Unavailable);

    private string FormatSupportingAvailability(
        MetricAvailability availability,
        string? detail = null)
    {
        string? safeDetail = SafeBrokerDetail(detail);
        return safeDetail ?? availability switch
        {
            MetricAvailability.AccessDenied => _localization.Get(LocalizationKeys.AccessDenied),
            MetricAvailability.Unsupported => _localization.Get(LocalizationKeys.Unsupported),
            MetricAvailability.Error => _localization.Get(LocalizationKeys.Error),
            _ => string.Empty
        };
    }

    private static string? SafeBrokerDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        string firstLine = detail.Split('\r', '\n')[0];
        return firstLine.StartsWith("Broker service not installed.", StringComparison.Ordinal)
            || firstLine.StartsWith("Broker service stopped.", StringComparison.Ordinal)
            || firstLine.StartsWith("Broker connection failed.", StringComparison.Ordinal)
            || firstLine.StartsWith("ETW unavailable.", StringComparison.Ordinal)
            || firstLine.StartsWith("No attributed activity yet.", StringComparison.Ordinal)
            || firstLine.StartsWith(
                "The privileged ETW broker protocol version is incompatible.",
                StringComparison.Ordinal)
            ? firstLine
            : firstLine.Contains("TraceEventSession.", StringComparison.Ordinal)
                ? "ETW unavailable."
                : null;
    }

    private static bool HasNoAttributedActivity(
        MetricValue<ulong> firstTotal,
        MetricValue<ulong> secondTotal) =>
        firstTotal.IsComplete
        && secondTotal.IsComplete
        && firstTotal.Value == 0
        && secondTotal.Value == 0;

    private string FormatRate(MetricValue<double> metric)
    {
        if (!metric.IsAvailable)
        {
            return metric.Availability == MetricAvailability.WarmingUp
                ? _localization.Get(LocalizationKeys.WarmingUp)
                : _localization.Get(LocalizationKeys.Unavailable);
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

    private string FormatAvailability<T>(MetricValue<T> metric)
        where T : struct => metric.Availability switch
        {
            MetricAvailability.Available => _localization.Get(LocalizationKeys.Available),
            MetricAvailability.Partial => _localization.Get(LocalizationKeys.PartialLowerBound),
            MetricAvailability.WarmingUp => _localization.Get(LocalizationKeys.WarmingUp),
            MetricAvailability.AccessDenied => _localization.Get(LocalizationKeys.AccessDenied),
            MetricAvailability.Unsupported => _localization.Get(LocalizationKeys.Unsupported),
            MetricAvailability.Unavailable => _localization.Get(LocalizationKeys.Unavailable),
            _ => _localization.Get(LocalizationKeys.Error)
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
