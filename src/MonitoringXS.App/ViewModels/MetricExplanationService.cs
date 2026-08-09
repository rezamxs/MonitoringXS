using MonitoringXS.App.Localization;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public enum MetricDescriptionId
{
    Cpu,
    Memory,
    ProcessIo,
    PhysicalDisk,
    Network,
    Gpu,
    DedicatedGpuMemory,
    SharedGpuMemory,
    History
}

public sealed record MetricExplanationItem(
    string Name,
    string BeginnerText,
    string StatusText,
    string AdvancedText,
    string ProviderName,
    bool IsHealthy = false);

public sealed class MetricExplanationService
{
    private readonly LocalizationService _localization;
    private readonly LiveRefreshCadence? _cadence;

    public MetricExplanationService(
        LocalizationService localization,
        LiveRefreshCadence? cadence = null)
    {
        _localization = localization;
        _cadence = cadence;
    }

    public IReadOnlyList<MetricExplanationItem> Create(
        ApplicationMetricSnapshot snapshot,
        int historySampleCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        double seconds = (_cadence?.Interval ?? TimeSpan.FromSeconds(1)).TotalSeconds;
        MetricAvailability processIo = Combine(
            snapshot.IoReadBytesPerSecond.Availability,
            snapshot.IoWriteBytesPerSecond.Availability);
        MetricAvailability physicalDisk = Combine(
            snapshot.PhysicalDisk.ReadBytesPerSecond.Availability,
            snapshot.PhysicalDisk.WriteBytesPerSecond.Availability);
        MetricAvailability network = Combine(
            snapshot.Network.DownloadBytesPerSecond.Availability,
            snapshot.Network.UploadBytesPerSecond.Availability);

        return
        [
            CreateItem(MetricDescriptionId.Cpu, snapshot.CpuPercent.Availability, seconds),
            CreateItem(MetricDescriptionId.Memory, snapshot.WorkingSetBytes.Availability, seconds),
            CreateItem(MetricDescriptionId.ProcessIo, processIo, seconds,
                snapshot.IoReadBytesPerSecond.Detail ?? snapshot.IoWriteBytesPerSecond.Detail),
            CreateItem(MetricDescriptionId.PhysicalDisk, physicalDisk, seconds,
                snapshot.PhysicalDisk.ReadBytesPerSecond.Detail ?? snapshot.PhysicalDisk.WriteBytesPerSecond.Detail),
            CreateItem(MetricDescriptionId.Network, network, seconds,
                snapshot.Network.DownloadBytesPerSecond.Detail ?? snapshot.Network.UploadBytesPerSecond.Detail,
                snapshot.Network.Reason),
            CreateItem(MetricDescriptionId.Gpu, snapshot.Gpu.UtilizationPercent.Availability, seconds,
                snapshot.Gpu.UtilizationPercent.Detail, gpuReason: snapshot.Gpu.Reason,
                providerName: snapshot.Gpu.Diagnostics.ProviderName),
            CreateItem(MetricDescriptionId.DedicatedGpuMemory, snapshot.Gpu.DedicatedMemoryBytes.Availability, seconds,
                snapshot.Gpu.DedicatedMemoryBytes.Detail, gpuReason: snapshot.Gpu.Reason,
                providerName: snapshot.Gpu.Diagnostics.ProviderName),
            CreateItem(MetricDescriptionId.SharedGpuMemory, snapshot.Gpu.SharedMemoryBytes.Availability, seconds,
                snapshot.Gpu.SharedMemoryBytes.Detail, gpuReason: snapshot.Gpu.Reason,
                providerName: snapshot.Gpu.Diagnostics.ProviderName),
            CreateItem(
                MetricDescriptionId.History,
                historySampleCount > 0 ? MetricAvailability.Available : MetricAvailability.WarmingUp,
                seconds,
                historySampleCount > 0 ? null : "History not collected yet.")
        ];
    }

    public string Reason(
        MetricDescriptionId metric,
        MetricAvailability availability,
        string? detail = null,
        NetworkAvailabilityReason networkReason = NetworkAvailabilityReason.None,
        GpuAvailabilityReason gpuReason = GpuAvailabilityReason.None)
    {
        string key = ReasonKey(metric, availability, detail, networkReason, gpuReason);
        return _localization.Get(key);
    }

    private MetricExplanationItem CreateItem(
        MetricDescriptionId metric,
        MetricAvailability availability,
        double seconds,
        string? detail = null,
        NetworkAvailabilityReason networkReason = NetworkAvailabilityReason.None,
        GpuAvailabilityReason gpuReason = GpuAvailabilityReason.None,
        string? providerName = null)
    {
        string status = Reason(metric, availability, detail, networkReason, gpuReason);
        bool isHealthy = availability == MetricAvailability.Available;
        return new(
            _localization.Get(NameKey(metric)),
            _localization.Get(ExplanationKey(metric)),
            status,
            _localization.Format(AdvancedKey(metric), seconds, status),
            string.IsNullOrWhiteSpace(providerName) ? DefaultProvider(metric) : providerName,
            isHealthy);
    }

    private static string DefaultProvider(MetricDescriptionId metric) => metric switch
    {
        MetricDescriptionId.Cpu => "Windows GetProcessTimes",
        MetricDescriptionId.Memory => "Windows process working-set data",
        MetricDescriptionId.ProcessIo => "Windows GetProcessIoCounters",
        MetricDescriptionId.PhysicalDisk => "Privileged Metrics Service / kernel disk provider (ETW)",
        MetricDescriptionId.Network => "Privileged Metrics Service / kernel network provider (ETW)",
        MetricDescriptionId.Gpu or MetricDescriptionId.DedicatedGpuMemory or MetricDescriptionId.SharedGpuMemory =>
            GpuCollectorDiagnostics.WindowsPdhProvider,
        _ => "SQLite local history"
    };

    private static MetricAvailability Combine(
        MetricAvailability first,
        MetricAvailability second)
    {
        if (first == MetricAvailability.Available && second == MetricAvailability.Available)
        {
            return MetricAvailability.Available;
        }

        if (first is MetricAvailability.Available or MetricAvailability.Partial
            || second is MetricAvailability.Available or MetricAvailability.Partial)
        {
            return MetricAvailability.Partial;
        }

        return AvailabilityRank(first) >= AvailabilityRank(second) ? first : second;
    }

    private static int AvailabilityRank(MetricAvailability availability) => availability switch
    {
        MetricAvailability.Available => 0,
        MetricAvailability.Partial => 1,
        MetricAvailability.WarmingUp => 2,
        MetricAvailability.Unavailable => 3,
        MetricAvailability.AccessDenied => 4,
        MetricAvailability.Unsupported => 5,
        _ => 6
    };

    private static string ReasonKey(
        MetricDescriptionId metric,
        MetricAvailability availability,
        string? detail,
        NetworkAvailabilityReason networkReason,
        GpuAvailabilityReason gpuReason)
    {
        if (networkReason == NetworkAvailabilityReason.SessionConflict)
        {
            return LocalizationKeys.ReasonSessionConflict;
        }

        if (networkReason == NetworkAvailabilityReason.EventLoss)
        {
            return LocalizationKeys.ReasonEventLoss;
        }

        if (gpuReason is GpuAvailabilityReason.ProcessExited or GpuAvailabilityReason.ProcessUnavailable)
        {
            return LocalizationKeys.ReasonProcessExited;
        }

        if (gpuReason is GpuAvailabilityReason.UnsupportedDriver
            or GpuAvailabilityReason.CounterSetUnavailable
            or GpuAvailabilityReason.CounterUnavailable)
        {
            return LocalizationKeys.ReasonDriverCounters;
        }

        string normalized = detail ?? string.Empty;
        if (normalized.Contains("service not installed", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonServiceNotInstalled;
        }

        if (normalized.Contains("service stopped", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonServiceStopped;
        }

        if (normalized.Contains("protocol", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("incompatible", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonProtocolMismatch;
        }

        if (normalized.Contains("connection failed", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonServiceConnection;
        }

        if (normalized.Contains("No attributed activity", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonNoAttributedData;
        }

        if (normalized.Contains("session", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("conflict", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonSessionConflict;
        }

        if (normalized.Contains("lost", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("dropped", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonEventLoss;
        }

        if (normalized.Contains("process exited", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonProcessExited;
        }

        if (metric == MetricDescriptionId.History
            && normalized.Contains("not collected", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationKeys.ReasonHistoryNotCollected;
        }

        return availability switch
        {
            MetricAvailability.Available => LocalizationKeys.ReasonAvailable,
            MetricAvailability.Partial => LocalizationKeys.ReasonPartial,
            MetricAvailability.WarmingUp => LocalizationKeys.ReasonWarmingUp,
            MetricAvailability.AccessDenied => LocalizationKeys.ReasonPermissionRequired,
            MetricAvailability.Unsupported => metric is MetricDescriptionId.Gpu
                or MetricDescriptionId.DedicatedGpuMemory
                or MetricDescriptionId.SharedGpuMemory
                ? LocalizationKeys.ReasonDriverCounters
                : LocalizationKeys.ReasonProviderUnsupported,
            MetricAvailability.Error => metric == MetricDescriptionId.History
                ? LocalizationKeys.ReasonHistoryDatabaseUnavailable
                : LocalizationKeys.ReasonCollectorError,
            _ => LocalizationKeys.ReasonTemporarilyUnavailable
        };
    }

    private static string NameKey(MetricDescriptionId metric) => metric switch
    {
        MetricDescriptionId.Cpu => LocalizationKeys.MetricCpu,
        MetricDescriptionId.Memory => LocalizationKeys.MetricWorkingSet,
        MetricDescriptionId.ProcessIo => LocalizationKeys.SortProcessIo,
        MetricDescriptionId.PhysicalDisk => LocalizationKeys.SortDisk,
        MetricDescriptionId.Network => LocalizationKeys.SortNetwork,
        MetricDescriptionId.Gpu => LocalizationKeys.MetricGpuUtilization,
        MetricDescriptionId.DedicatedGpuMemory => LocalizationKeys.MetricGpuDedicated,
        MetricDescriptionId.SharedGpuMemory => LocalizationKeys.MetricGpuShared,
        _ => LocalizationKeys.HistoryPageTitleText
    };

    private static string ExplanationKey(MetricDescriptionId metric) => metric switch
    {
        MetricDescriptionId.Cpu => LocalizationKeys.MetricExplanationCpu,
        MetricDescriptionId.Memory => LocalizationKeys.MetricExplanationMemory,
        MetricDescriptionId.ProcessIo => LocalizationKeys.MetricExplanationProcessIo,
        MetricDescriptionId.PhysicalDisk => LocalizationKeys.MetricExplanationPhysicalDisk,
        MetricDescriptionId.Network => LocalizationKeys.MetricExplanationNetwork,
        MetricDescriptionId.Gpu => LocalizationKeys.MetricExplanationGpu,
        MetricDescriptionId.DedicatedGpuMemory => LocalizationKeys.MetricExplanationGpuDedicated,
        MetricDescriptionId.SharedGpuMemory => LocalizationKeys.MetricExplanationGpuShared,
        _ => LocalizationKeys.MetricExplanationHistory
    };

    private static string AdvancedKey(MetricDescriptionId metric) => metric switch
    {
        MetricDescriptionId.Cpu => LocalizationKeys.MetricAdvancedCpu,
        MetricDescriptionId.Memory => LocalizationKeys.MetricAdvancedMemory,
        MetricDescriptionId.ProcessIo => LocalizationKeys.MetricAdvancedProcessIo,
        MetricDescriptionId.PhysicalDisk => LocalizationKeys.MetricAdvancedPhysicalDisk,
        MetricDescriptionId.Network => LocalizationKeys.MetricAdvancedNetwork,
        MetricDescriptionId.Gpu => LocalizationKeys.MetricAdvancedGpu,
        MetricDescriptionId.DedicatedGpuMemory => LocalizationKeys.MetricAdvancedGpuDedicated,
        MetricDescriptionId.SharedGpuMemory => LocalizationKeys.MetricAdvancedGpuShared,
        _ => LocalizationKeys.MetricAdvancedHistory
    };
}
