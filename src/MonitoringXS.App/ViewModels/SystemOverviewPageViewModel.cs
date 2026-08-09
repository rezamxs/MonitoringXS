using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.App.Controls;
using MonitoringXS.App.Localization;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed partial class SystemOverviewMetricCardViewModel : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryValue { get; set; } = string.Empty;
    [ObservableProperty] public partial string SecondaryLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string SecondaryValue { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasSecondaryValue { get; set; }
    [ObservableProperty] public partial bool HasSecondarySeries { get; set; }
    [ObservableProperty] public partial bool HasStatus { get; set; }
    [ObservableProperty] public partial string StatusLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ChartSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string ChartEmptyText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryChartAutomationName { get; set; } = string.Empty;
    [ObservableProperty] public partial string SecondaryChartAutomationName { get; set; } = string.Empty;

    public string IconGlyph { get; init; } = string.Empty;

    public ObservableCollection<CpuHistorySample> PrimarySamples { get; } = [];

    public ObservableCollection<CpuHistorySample> SecondarySamples { get; } = [];

    public MetricSparklineScale ChartScale { get; init; }

    public string UnitText { get; init; } = string.Empty;
}

public sealed partial class SystemOverviewPageViewModel : ObservableObject
{
    private const int HistoryCapacity = 60;
    private readonly LocalizationService _localization;
    private readonly SystemOverviewMetricCardViewModel _cpu;
    private readonly SystemOverviewMetricCardViewModel _memory;
    private readonly SystemOverviewMetricCardViewModel _disk;
    private readonly SystemOverviewMetricCardViewModel _network;
    private readonly SystemOverviewMetricCardViewModel _gpu;
    private SystemOverviewSnapshot? _lastSnapshot;
    private IReadOnlyList<SystemOverviewHistoryPoint> _lastHistory = [];
    private string _available = string.Empty;
    private string _partialLabel = string.Empty;
    private string _partialValue = string.Empty;
    private string _warmingUpValue = string.Empty;
    private string _unavailableValue = string.Empty;
    private string _unsupportedValue = string.Empty;
    private string _accessDeniedValue = string.Empty;
    private string _errorValue = string.Empty;
    private string _partial = string.Empty;
    private string _warmingUp = string.Empty;
    private string _unavailable = string.Empty;
    private string _unsupported = string.Empty;
    private string _accessDenied = string.Empty;
    private string _error = string.Empty;

    public SystemOverviewPageViewModel(LocalizationService localization)
    {
        _localization = localization;
        _cpu = new() { ChartScale = MetricSparklineScale.Percent, IconGlyph = "\uE9F5" };
        _memory = new() { ChartScale = MetricSparklineScale.Percent, HasSecondaryValue = true, IconGlyph = "\uE950" };
        _disk = new()
        {
            ChartScale = MetricSparklineScale.Dynamic,
            UnitText = "bytes/s",
            HasSecondaryValue = true,
            HasSecondarySeries = true,
            IconGlyph = "\uEDA2"
        };
        _network = new()
        {
            ChartScale = MetricSparklineScale.Dynamic,
            UnitText = "bytes/s",
            HasSecondaryValue = true,
            HasSecondarySeries = true,
            IconGlyph = "\uE968"
        };
        _gpu = new() { ChartScale = MetricSparklineScale.Percent, IconGlyph = "\uE943" };
        SummaryCards = [_cpu, _memory, _disk, _network, _gpu];
        PrimaryCards = [_cpu, _memory];
        SecondaryCards = [_disk, _network, _gpu];
        Relocalize();
    }

    [ObservableProperty] public partial string PageTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string Subtitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryMetricsTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string SecondaryMetricsTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ChartHelpText { get; set; } = string.Empty;

    public IReadOnlyList<SystemOverviewMetricCardViewModel> SummaryCards { get; }

    public IReadOnlyList<SystemOverviewMetricCardViewModel> PrimaryCards { get; }

    public IReadOnlyList<SystemOverviewMetricCardViewModel> SecondaryCards { get; }

    public void Update(
        SystemOverviewSnapshot? snapshot,
        IReadOnlyList<SystemOverviewHistoryPoint> history)
    {
        _lastSnapshot = snapshot;
        _lastHistory = history;
        UpdateValues(snapshot);
        SyncSeries(_cpu.PrimarySamples, history, HistoryMetric.Cpu);
        SyncSeries(_memory.PrimarySamples, history, HistoryMetric.Memory);
        SyncSeries(_disk.PrimarySamples, history, HistoryMetric.DiskRead);
        SyncSeries(_disk.SecondarySamples, history, HistoryMetric.DiskWrite);
        SyncSeries(_network.PrimarySamples, history, HistoryMetric.NetworkReceive);
        SyncSeries(_network.SecondarySamples, history, HistoryMetric.NetworkSend);
        SyncSeries(_gpu.PrimarySamples, history, HistoryMetric.Gpu);
    }

    public void Relocalize()
    {
        _available = _localization.Get(LocalizationKeys.Available);
        _partialLabel = _localization.Get(LocalizationKeys.SystemOverviewStatusPartialLabel);
        _partialValue = _localization.Get(LocalizationKeys.PartialLowerBound);
        _warmingUpValue = _localization.Get(LocalizationKeys.WarmingUp);
        _unavailableValue = _localization.Get(LocalizationKeys.Unavailable);
        _unsupportedValue = _localization.Get(LocalizationKeys.Unsupported);
        _accessDeniedValue = _localization.Get(LocalizationKeys.AccessDenied);
        _errorValue = _localization.Get(LocalizationKeys.Error);
        _partial = _localization.Get(LocalizationKeys.SystemOverviewStatusPartial);
        _warmingUp = _localization.Get(LocalizationKeys.SystemOverviewStatusWarmingUp);
        _unavailable = _localization.Get(LocalizationKeys.SystemOverviewStatusUnavailable);
        _unsupported = _localization.Get(LocalizationKeys.SystemOverviewStatusUnsupported);
        _accessDenied = _localization.Get(LocalizationKeys.SystemOverviewStatusAccessDenied);
        _error = _localization.Get(LocalizationKeys.SystemOverviewStatusError);
        PageTitle = _localization.Get(LocalizationKeys.SystemOverviewPageTitle);
        Subtitle = _localization.Get(LocalizationKeys.SystemOverviewSubtitle);
        PrimaryMetricsTitle = _localization.Get(LocalizationKeys.SystemOverviewPrimaryMetrics);
        SecondaryMetricsTitle = _localization.Get(LocalizationKeys.SystemOverviewSecondaryMetrics);
        ChartHelpText = _localization.Get(LocalizationKeys.SystemOverviewChartSummary);

        ConfigureCard(_cpu, LocalizationKeys.SystemOverviewCpu, LocalizationKeys.SystemOverviewUtilization, null);
        ConfigureCard(_memory, LocalizationKeys.SystemOverviewMemory, LocalizationKeys.SystemOverviewUsed, LocalizationKeys.SystemOverviewAvailableMemory);
        ConfigureCard(_disk, LocalizationKeys.SystemOverviewDisk, LocalizationKeys.SystemOverviewRead, LocalizationKeys.SystemOverviewWrite);
        ConfigureCard(_network, LocalizationKeys.SystemOverviewNetwork, LocalizationKeys.SystemOverviewReceive, LocalizationKeys.SystemOverviewSend);
        ConfigureCard(_gpu, LocalizationKeys.SystemOverviewGpu, LocalizationKeys.SystemOverviewUtilization, null);
        Update(_lastSnapshot, _lastHistory);
    }

    private void ConfigureCard(
        SystemOverviewMetricCardViewModel card,
        string titleKey,
        string primaryLabelKey,
        string? secondaryLabelKey)
    {
        card.Title = _localization.Get(titleKey);
        card.PrimaryLabel = _localization.Get(primaryLabelKey);
        card.SecondaryLabel = secondaryLabelKey is null ? string.Empty : _localization.Get(secondaryLabelKey);
        card.ChartSummary = _localization.Get(LocalizationKeys.SystemOverviewChartSummary);
        card.ChartEmptyText = _localization.Get(LocalizationKeys.SystemOverviewChartEmpty);
        card.PrimaryChartAutomationName = _localization.Format(
            LocalizationKeys.SystemOverviewChartAutomationName,
            card.Title,
            card.PrimaryLabel);
        card.SecondaryChartAutomationName = secondaryLabelKey is null
            ? string.Empty
            : _localization.Format(
                LocalizationKeys.SystemOverviewChartAutomationName,
                card.Title,
                card.SecondaryLabel);
    }

    private void UpdateValues(SystemOverviewSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            foreach (SystemOverviewMetricCardViewModel card in PrimaryCards.Concat(SecondaryCards))
            {
                card.PrimaryValue = _unavailable;
                card.SecondaryValue = _unavailable;
                SetStatus(card, MetricAvailability.Unavailable);
            }
            return;
        }

        bool invalidCpu = snapshot.TotalCpuPercent.IsAvailable
            && !CanDisplayPercent(snapshot.TotalCpuPercent);
        _cpu.PrimaryValue = FormatPercent(snapshot.TotalCpuPercent);
        SetStatus(_cpu, invalidCpu ? MetricAvailability.Error : snapshot.Diagnostics.CpuAvailability);

        bool invalidMemory = !IsValidMemory(snapshot);
        _memory.PrimaryValue = _localization.Format(
            LocalizationKeys.SystemOverviewMemoryUsedFormat,
            FormatBytes(snapshot.UsedPhysicalMemoryBytes),
            FormatBytes(snapshot.TotalPhysicalMemoryBytes));
        _memory.SecondaryValue = FormatBytes(snapshot.AvailablePhysicalMemoryBytes);
        SetStatus(_memory, invalidMemory ? MetricAvailability.Error : snapshot.Diagnostics.MemoryAvailability);

        bool invalidDisk = snapshot.DiskReadBytesPerSecond.IsAvailable
                && !CanDisplayRate(snapshot.DiskReadBytesPerSecond)
            || snapshot.DiskWriteBytesPerSecond.IsAvailable
                && !CanDisplayRate(snapshot.DiskWriteBytesPerSecond);
        _disk.PrimaryValue = FormatRate(snapshot.DiskReadBytesPerSecond);
        _disk.SecondaryValue = FormatRate(snapshot.DiskWriteBytesPerSecond);
        SetStatus(_disk, invalidDisk ? MetricAvailability.Error : snapshot.Diagnostics.DiskAvailability);

        bool invalidNetwork = snapshot.NetworkReceiveBytesPerSecond.IsAvailable
                && !CanDisplayRate(snapshot.NetworkReceiveBytesPerSecond)
            || snapshot.NetworkSendBytesPerSecond.IsAvailable
                && !CanDisplayRate(snapshot.NetworkSendBytesPerSecond);
        _network.PrimaryValue = FormatRate(snapshot.NetworkReceiveBytesPerSecond);
        _network.SecondaryValue = FormatRate(snapshot.NetworkSendBytesPerSecond);
        SetStatus(_network, invalidNetwork ? MetricAvailability.Error : snapshot.Diagnostics.NetworkAvailability);

        bool invalidGpu = snapshot.GpuUtilizationPercent.IsAvailable
            && !CanDisplayPercent(snapshot.GpuUtilizationPercent);
        _gpu.PrimaryValue = FormatPercent(snapshot.GpuUtilizationPercent);
        SetStatus(_gpu, invalidGpu ? MetricAvailability.Error : snapshot.Diagnostics.GpuAvailability);
    }

    private void SetStatus(SystemOverviewMetricCardViewModel card, MetricAvailability availability)
    {
        // Partial status hides the visible badge but keeps the ≥ prefix in the value
        // and the technical explanation in the tooltip for accessibility.
        card.HasStatus = availability != MetricAvailability.Available
            && availability != MetricAvailability.Partial;
        card.StatusLabel = availability == MetricAvailability.Partial
            ? string.Empty
            : AvailabilityText(availability);
        card.StatusText = availability switch
        {
            MetricAvailability.Available => string.Empty,
            MetricAvailability.Partial => _partial,
            MetricAvailability.WarmingUp => _warmingUp,
            MetricAvailability.AccessDenied => _accessDenied,
            MetricAvailability.Unsupported => _unsupported,
            MetricAvailability.Unavailable => _unavailable,
            _ => _error
        };
    }

    private string FormatPercent(MetricValue<double> metric) => CanDisplayPercent(metric)
        ? $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
        : AvailabilityText(metric.IsAvailable ? MetricAvailability.Error : metric.Availability);

    private string FormatRate(MetricValue<double> metric) => CanDisplayRate(metric)
        ? PartialPrefix(metric) + FormatScaled(metric.Value!.Value, true)
        : AvailabilityText(metric.IsAvailable ? MetricAvailability.Error : metric.Availability);

    private string FormatBytes(MetricValue<long> metric) => metric.IsAvailable && metric.Value >= 0
        ? PartialPrefix(metric) + FormatScaled(metric.Value!.Value, false)
        : AvailabilityText(metric.IsAvailable ? MetricAvailability.Error : metric.Availability);

    private string AvailabilityText(MetricAvailability availability) => availability switch
    {
        MetricAvailability.Available => _available,
        MetricAvailability.Partial => _partialValue,
        MetricAvailability.WarmingUp => _warmingUpValue,
        MetricAvailability.AccessDenied => _accessDeniedValue,
        MetricAvailability.Unsupported => _unsupportedValue,
        MetricAvailability.Unavailable => _unavailableValue,
        _ => _errorValue
    };

    private static string FormatScaled(double bytes, bool perSecond)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        string number = bytes.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return perSecond ? $"{number} {units[unit]}/s" : $"{number} {units[unit]}";
    }

    private static bool CanDisplayPercent(MetricValue<double> metric) => metric.IsAvailable
        && metric.Value is >= 0 and <= 100
        && double.IsFinite(metric.Value.Value);

    private static bool CanDisplayRate(MetricValue<double> metric) => metric.IsAvailable
        && metric.Value >= 0
        && double.IsFinite(metric.Value.Value);

    private static bool IsValidMemory(SystemOverviewSnapshot snapshot)
    {
        if (!snapshot.TotalPhysicalMemoryBytes.IsAvailable
            || !snapshot.UsedPhysicalMemoryBytes.IsAvailable
            || !snapshot.AvailablePhysicalMemoryBytes.IsAvailable
            || !snapshot.PhysicalMemoryUtilizationPercent.IsAvailable)
        {
            return true;
        }

        long total = snapshot.TotalPhysicalMemoryBytes.Value!.Value;
        long used = snapshot.UsedPhysicalMemoryBytes.Value!.Value;
        long available = snapshot.AvailablePhysicalMemoryBytes.Value!.Value;
        return total > 0
            && used is >= 0
            && available is >= 0
            && used <= total
            && available <= total
            && CanDisplayPercent(snapshot.PhysicalMemoryUtilizationPercent);
    }

    private static string PartialPrefix<T>(MetricValue<T> metric) where T : struct =>
        metric.Availability == MetricAvailability.Partial ? "≥ " : string.Empty;

    private static void SyncSeries(
        ObservableCollection<CpuHistorySample> samples,
        IReadOnlyList<SystemOverviewHistoryPoint> history,
        HistoryMetric metric)
    {
        int start = Math.Max(0, history.Count - HistoryCapacity);
        int desiredCount = history.Count - start;
        DateTimeOffset firstTimestamp = desiredCount == 0 ? default : history[start].Timestamp;
        while (samples.Count > 0 && (desiredCount == 0 || samples[0].Timestamp < firstTimestamp))
        {
            samples.RemoveAt(0);
        }

        for (int index = 0; index < desiredCount; index++)
        {
            SystemOverviewHistoryPoint point = history[start + index];
            double? value = Sanitize(SelectValue(point, metric), metric);
            if (index < samples.Count && samples[index].Timestamp != point.Timestamp)
            {
                while (samples.Count > index)
                {
                    samples.RemoveAt(samples.Count - 1);
                }
            }

            if (index == samples.Count)
            {
                samples.Add(new(point.Timestamp, value));
            }
            else if (samples[index].Value != value)
            {
                samples[index] = new(point.Timestamp, value);
            }
        }

        while (samples.Count > desiredCount)
        {
            samples.RemoveAt(samples.Count - 1);
        }
    }

    private static double? SelectValue(SystemOverviewHistoryPoint point, HistoryMetric metric) => metric switch
    {
        HistoryMetric.Cpu => point.CpuPercent,
        HistoryMetric.Memory => point.MemoryUtilizationPercent,
        HistoryMetric.DiskRead => point.DiskReadBytesPerSecond,
        HistoryMetric.DiskWrite => point.DiskWriteBytesPerSecond,
        HistoryMetric.NetworkReceive => point.NetworkReceiveBytesPerSecond,
        HistoryMetric.NetworkSend => point.NetworkSendBytesPerSecond,
        HistoryMetric.Gpu => point.GpuUtilizationPercent,
        _ => null
    };

    private static double? Sanitize(double? value, HistoryMetric metric)
    {
        if (value is not { } real || !double.IsFinite(real) || real < 0)
        {
            return null;
        }

        bool percent = metric is HistoryMetric.Cpu or HistoryMetric.Memory or HistoryMetric.Gpu;
        return percent && real > 100 ? null : real;
    }

    private enum HistoryMetric
    {
        Cpu,
        Memory,
        DiskRead,
        DiskWrite,
        NetworkReceive,
        NetworkSend,
        Gpu
    }
}
