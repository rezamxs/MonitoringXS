using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MonitoringXS.App.Localization;
using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using Windows.Storage.Streams;

namespace MonitoringXS.App.ViewModels;

public sealed partial class ApplicationCardViewModel : ObservableObject, IApplicationListItemViewModel
{
    private readonly LocalizationService _localization;
    private readonly MetricExplanationService _metricExplanations;
    private readonly IApplicationIconProvider? _iconProvider;
    private string? _currentIconPath;

    public ApplicationCardViewModel(
        LocalizationService? localization = null,
        MetricExplanationService? metricExplanations = null,
        IApplicationIconProvider? iconProvider = null)
    {
        _localization = localization ?? new LocalizationService();
        _metricExplanations = metricExplanations ?? new MetricExplanationService(_localization);
        _iconProvider = iconProvider;
    }
    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Publisher { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppIcon))]
    [NotifyPropertyChangedFor(nameof(HasFallbackIcon))]
    public partial ImageSource? AppIconSource { get; set; }

    public bool HasAppIcon => AppIconSource is not null;

    public bool HasFallbackIcon => AppIconSource is null;

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
        TryLoadIcon(snapshot);
        ScalarPresentation cpu = FormatCpu(snapshot.CpuPercent);
        CpuText = cpu.ValueText;
        CpuStatusText = cpu.StatusText;
        ScalarPresentation memory = FormatBytes(snapshot.WorkingSetBytes);
        MemoryText = memory.ValueText;
        MemoryStatusText = memory.StatusText;
        MetricPairPresentation io = FormatRatePair(
            MetricDescriptionId.ProcessIo,
            snapshot.IoReadBytesPerSecond,
            "read",
            snapshot.IoWriteBytesPerSecond,
            "write");
        IoText = io.ValueText;
        IoStatusText = io.StatusText;
        MetricPairPresentation physicalDisk = FormatRatePair(
            MetricDescriptionId.PhysicalDisk,
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
            MetricDescriptionId.Network,
            snapshot.Network.DownloadBytesPerSecond,
            "receive",
            snapshot.Network.UploadBytesPerSecond,
            "send",
            HasNoAttributedActivity(
                snapshot.Network.SessionDownloadedBytes,
                snapshot.Network.SessionUploadedBytes),
            snapshot.Network.Reason);
        NetworkText = network.ValueText;
        NetworkStatusText = network.StatusText;
        ScalarPresentation gpu = FormatGpu(
            snapshot.Gpu.UtilizationPercent,
            snapshot.Gpu.Reason);
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
            return FormatUnavailableScalar(metric, MetricDescriptionId.Cpu);
        }

        string value = $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%";
        return FormatAvailableScalar(value, metric);
    }

    private ScalarPresentation FormatBytes(MetricValue<long> metric)
    {
        if (!metric.IsAvailable)
        {
            return FormatUnavailableScalar(metric, MetricDescriptionId.Memory);
        }

        double bytes = metric.Value!.Value;
        string value = PartialPrefix(metric) + (bytes >= 1024d * 1024d * 1024d
            ? $"{(bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture)} MB");
        return FormatAvailableScalar(value, metric);
    }

    private ScalarPresentation FormatGpu(
        MetricValue<double> metric,
        GpuAvailabilityReason reason)
    {
        if (!metric.IsAvailable)
        {
            return FormatUnavailableScalar(
                metric,
                MetricDescriptionId.Gpu,
                gpuReason: reason);
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

    private ScalarPresentation FormatUnavailableScalar<T>(
        MetricValue<T> metric,
        MetricDescriptionId description,
        NetworkAvailabilityReason networkReason = NetworkAvailabilityReason.None,
        GpuAvailabilityReason gpuReason = GpuAvailabilityReason.None)
        where T : struct
    {
        string reason = _metricExplanations.Reason(
            description,
            metric.Availability,
            metric.Detail,
            networkReason,
            gpuReason);
        return new(
            FormatCompactUnavailable(metric.Availability),
            reason,
            reason);
    }

    private void TryLoadIcon(ApplicationMetricSnapshot snapshot)
    {
        if (_iconProvider is null)
        {
            return;
        }

        string? executablePath = snapshot.Processes
            .Select(process => process.ExecutablePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? snapshot.Application.InstallationPath;
        if (string.IsNullOrWhiteSpace(executablePath)
            || string.Equals(executablePath, _currentIconPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentIconPath = executablePath;
        _ = LoadIconAsync(executablePath);
    }

    private async Task LoadIconAsync(string executablePath)
    {
        try
        {
            ApplicationIconData? iconData = await _iconProvider!.GetIconAsync(
                executablePath,
                32,
                CancellationToken.None);
            if (iconData is null)
            {
                return;
            }

            InMemoryRandomAccessStream stream = new();
            using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(iconData.Content.ToArray());
                await writer.StoreAsync();
            }

            stream.Seek(0);
            BitmapImage bitmap = new();
            await bitmap.SetSourceAsync(stream);
            if (string.Equals(executablePath, _currentIconPath, StringComparison.OrdinalIgnoreCase))
            {
                AppIconSource = bitmap;
            }
        }
        catch
        {
            // Icon loading is best-effort; the native fallback remains visible.
        }
    }

    private MetricPairPresentation FormatRatePair(
        MetricDescriptionId description,
        MetricValue<double> first,
        string firstDirection,
        MetricValue<double> second,
        string secondDirection,
        bool noAttributedActivityYet = false,
        NetworkAvailabilityReason networkReason = NetworkAvailabilityReason.None)
    {
        if (!first.IsAvailable
            && !second.IsAvailable
            && first.Availability == second.Availability)
        {
            return new(
                FormatCompactUnavailable(first.Availability),
                _metricExplanations.Reason(
                    description,
                    first.Availability,
                    first.Detail ?? second.Detail,
                    networkReason),
                _metricExplanations.Reason(
                    description,
                    first.Availability,
                    first.Detail ?? second.Detail,
                    networkReason));
        }

        string values = $"{FormatDirectionalRate(first, firstDirection)} · {FormatDirectionalRate(second, secondDirection)}";
        string accessibleValues =
            $"{FormatAccessibleDirectionalRate(first, firstDirection)}, {FormatAccessibleDirectionalRate(second, secondDirection)}";
        string[] statuses =
        [
            FormatMetricStatus(first, description, networkReason),
            FormatMetricStatus(second, description, networkReason),
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

    private string FormatMetricStatus<T>(
        MetricValue<T> metric,
        MetricDescriptionId description,
        NetworkAvailabilityReason networkReason)
        where T : struct => metric.Availability == MetricAvailability.Partial
        ? _localization.Get(LocalizationKeys.PartialLowerBound)
        : metric.Availability == MetricAvailability.Available
            ? string.Empty
            : _metricExplanations.Reason(
                description,
                metric.Availability,
                metric.Detail,
                networkReason);

    private string FormatCompactUnavailable(MetricAvailability availability) =>
        availability == MetricAvailability.WarmingUp
            ? _localization.Get(LocalizationKeys.WarmingUp)
            : _localization.Get(LocalizationKeys.Unavailable);

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
