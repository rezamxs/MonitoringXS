using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MonitoringXS.App.Controls;
using MonitoringXS.App.Localization;
using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using Windows.Storage.Streams;

namespace MonitoringXS.App.ViewModels;

public sealed partial class ApplicationTabViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;
    private readonly MetricExplanationService _metricExplanations;
    private readonly IApplicationIconProvider? _iconProvider;
    private string? _currentIconPath;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppIcon))]
    [NotifyPropertyChangedFor(nameof(HasFallbackIcon))]
    public partial ImageSource? AppIconSource { get; set; }

    public bool HasAppIcon => AppIconSource is not null;

    public bool HasFallbackIcon => AppIconSource is null;

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
    public partial string GpuUsageText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string DedicatedGpuMemoryText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string SharedGpuMemoryText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string GpuStatusText { get; set; } = "Warming up";

    [ObservableProperty]
    public partial string GpuDiagnosticsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProcessSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClassificationReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClassificationConfidence { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CpuStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IoStatusText { get; set; } = string.Empty;

    public ObservableCollection<CpuHistorySample> CpuSamples { get; } = [];

    public ObservableCollection<CpuHistorySample> MemorySamples { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<MetricExplanationViewModel> MetricExplanations { get; set; } = [];

    public ApplicationTabViewModel(
        string logicalApplicationId,
        string title,
        ProcessActionsViewModel? processActions = null,
        LocalizationService? localization = null,
        MetricExplanationService? metricExplanations = null,
        IApplicationIconProvider? iconProvider = null)
    {
        LogicalApplicationId = logicalApplicationId;
        Title = title;
        ProcessActions = processActions;
        _localization = localization ?? new LocalizationService();
        _metricExplanations = metricExplanations ?? new MetricExplanationService(_localization);
        _iconProvider = iconProvider;
    }

    public string LogicalApplicationId { get; }

    public ProcessActionsViewModel? ProcessActions { get; }

    public void Update(ApplicationMetricSnapshot snapshot, IReadOnlyList<ApplicationHistoryPoint> history)
    {
        _lastSnapshot = snapshot;
        _lastHistory = history;
        ProcessActions?.Update(snapshot);
        Title = snapshot.Application.DisplayName;
        CpuText = snapshot.CpuPercent.IsAvailable
            ? $"{PartialPrefix(snapshot.CpuPercent)}{snapshot.CpuPercent.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
            : snapshot.CpuPercent.Availability == MetricAvailability.WarmingUp ? "Warming up" : "Unavailable";
        CpuStatusText = FormatAvailability(snapshot.CpuPercent);
        MemoryText = FormatMemory(snapshot.WorkingSetBytes);
        MemoryStatusText = FormatAvailability(snapshot.WorkingSetBytes);
        IoReadText = FormatRate(snapshot.IoReadBytesPerSecond);
        IoWriteText = FormatRate(snapshot.IoWriteBytesPerSecond);
        IoStatusText = FormatIoStatus(snapshot.IoReadBytesPerSecond, snapshot.IoWriteBytesPerSecond);
        PhysicalDiskReadText = FormatRate(snapshot.PhysicalDisk.ReadBytesPerSecond);
        PhysicalDiskWriteText = FormatRate(snapshot.PhysicalDisk.WriteBytesPerSecond);
        PhysicalDiskStatusText = FormatAvailability(snapshot.PhysicalDisk.ReadBytesPerSecond);
        PhysicalDiskTotalsText = $"Read {FormatBytes(snapshot.PhysicalDisk.SessionReadBytes)} / Write {FormatBytes(snapshot.PhysicalDisk.SessionWriteBytes)}";
        PhysicalDiskOperationsText = $"Read {FormatCount(snapshot.PhysicalDisk.SessionReadOperationCount)} / Write {FormatCount(snapshot.PhysicalDisk.SessionWriteOperationCount)}";
        PhysicalDiskCollectorDiagnostics diagnostics = snapshot.PhysicalDisk.Diagnostics;
        string lastPhysicalDiskEvent = diagnostics.LastSuccessfulEventTimestampUtc?.ToString(
            "O",
            CultureInfo.InvariantCulture) ?? "none";
        string physicalDiskCompleteness = diagnostics.CollectorStatus is MetricAvailability.Available or MetricAvailability.Partial
            ? diagnostics.SessionTotalsAreLowerBounds ? "lower bound" : "complete"
            : "unavailable";
        PhysicalDiskDiagnosticsText = $"Status {diagnostics.CollectorStatus?.ToString() ?? "Unavailable"}; events {diagnostics.EventsObserved} ({diagnostics.ReadEventsObserved} read / {diagnostics.WriteEventsObserved} write); observed bytes {FormatBytes(MetricValue<ulong>.Available(diagnostics.ReadBytesObserved))} read / {FormatBytes(MetricValue<ulong>.Available(diagnostics.WriteBytesObserved))} write; rate {diagnostics.EventRatePerSecond.ToString("0", CultureInfo.InvariantCulture)}/s; queue {diagnostics.CurrentQueueDepth}/{diagnostics.MaximumQueueDepth} max; dropped {diagnostics.QueueEventsDropped}; ETW lost {diagnostics.EtwEventsLost}; buffer {diagnostics.EtwBufferSizeMegabytes} MB; unattributed {diagnostics.UnattributedEvents}; PID-reuse rejected {diagnostics.PidReuseEventsRejected}; metadata lookup failures {diagnostics.MetadataLookupFailures}; session start failures {diagnostics.SessionStartFailures}; access denied {diagnostics.AccessDeniedFailures}; processing {diagnostics.ProcessingLatencyMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms; last event {lastPhysicalDiskEvent}; completeness {physicalDiskCompleteness}.";
        NetworkDownloadText = FormatRate(snapshot.Network.DownloadBytesPerSecond);
        NetworkUploadText = FormatRate(snapshot.Network.UploadBytesPerSecond);
        NetworkStatusText = $"{FormatAvailability(snapshot.Network.DownloadBytesPerSecond)}; reason {snapshot.Network.Reason}.";
        NetworkTotalsText = $"Downloaded {FormatBytes(snapshot.Network.SessionDownloadedBytes)} / Uploaded {FormatBytes(snapshot.Network.SessionUploadedBytes)}";
        NetworkEndpointsText = $"TCP connections {FormatCount(snapshot.Network.ActiveTcpConnectionCount)} / UDP endpoints {FormatCount(snapshot.Network.UdpEndpointCount)}";
        NetworkCollectorDiagnostics networkDiagnostics = snapshot.Network.Diagnostics;
        string lastNetworkEvent = networkDiagnostics.LastSuccessfulEventTimestampUtc?.ToString(
            "O",
            CultureInfo.InvariantCulture) ?? "none";
        string networkCompleteness = networkDiagnostics.CollectorStatus is MetricAvailability.Available or MetricAvailability.Partial
            ? networkDiagnostics.SessionTotalsAreLowerBounds ? "lower bound" : "complete"
            : "unavailable";
        string networkStatusDetail = string.IsNullOrWhiteSpace(networkDiagnostics.CollectorStatusReason)
            ? "none"
            : networkDiagnostics.CollectorStatusReason;
        NetworkDiagnosticsText = $"Status {networkDiagnostics.CollectorStatus}; reason {networkDiagnostics.Reason}; detail {networkStatusDetail}; events {networkDiagnostics.EventsObserved} ({networkDiagnostics.SendEvents} send / {networkDiagnostics.ReceiveEvents} receive; TCP {networkDiagnostics.TcpSendEvents} send / {networkDiagnostics.TcpReceiveEvents} receive; UDP {networkDiagnostics.UdpSendEvents} send / {networkDiagnostics.UdpReceiveEvents} receive; IPv4 {networkDiagnostics.IPv4Events} / IPv6 {networkDiagnostics.IPv6Events}); source bytes {FormatBytes(MetricValue<ulong>.Available(networkDiagnostics.TotalSourceSendBytes))} send / {FormatBytes(MetricValue<ulong>.Available(networkDiagnostics.TotalSourceReceiveBytes))} receive; attributed {networkDiagnostics.AttributedEvents}; unattributed {networkDiagnostics.UnattributedEvents} (system {networkDiagnostics.SystemProcessEvents}; outside app set {networkDiagnostics.OutsideApplicationSetEvents}; unknown {networkDiagnostics.UnknownProcessEvents}; PID-reuse rejected {networkDiagnostics.PidReuseEventsRejected}); rate {networkDiagnostics.EventRatePerSecond.ToString("0", CultureInfo.InvariantCulture)}/s; queue {networkDiagnostics.CurrentQueueDepth}/{networkDiagnostics.MaximumQueueDepth} max of {networkDiagnostics.QueueCapacity}; dropped {networkDiagnostics.QueueEventsDropped}; ETW lost {networkDiagnostics.EtwEventsLost}; processing failures {networkDiagnostics.EventProcessingFailures}; unsupported versions {networkDiagnostics.UnsupportedEventVersions}; latency {networkDiagnostics.AverageProcessingLatencyMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms avg / {networkDiagnostics.MaximumProcessingLatencyMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms max; last event {lastNetworkEvent}; completeness {networkCompleteness}.";
        GpuUsageText = FormatPercent(snapshot.Gpu.UtilizationPercent);
        DedicatedGpuMemoryText = FormatBytes(snapshot.Gpu.DedicatedMemoryBytes);
        SharedGpuMemoryText = FormatBytes(snapshot.Gpu.SharedMemoryBytes);
        GpuStatusText = $"Utilization {FormatAvailability(snapshot.Gpu.UtilizationPercent)}; dedicated memory {FormatAvailability(snapshot.Gpu.DedicatedMemoryBytes)}; shared memory {FormatAvailability(snapshot.Gpu.SharedMemoryBytes)}; reason {snapshot.Gpu.Reason}.";
        GpuCollectorDiagnostics gpuDiagnostics = snapshot.Gpu.Diagnostics;
        string gpuDetail = string.IsNullOrWhiteSpace(gpuDiagnostics.CollectorStatusReason)
            ? "none"
            : gpuDiagnostics.CollectorStatusReason;
        string busiestEngine = snapshot.Gpu.BusiestEngine is GpuEngineId engine
            ? $"{engine.EngineType}, adapter LUID 0x{engine.AdapterLuid:X16}, physical index {engine.PhysicalAdapterIndex}"
            : "none";
        GpuDiagnosticsText = $"Provider {gpuDiagnostics.ProviderName}; status {gpuDiagnostics.CollectorStatus}; reason {gpuDiagnostics.Reason}; detail {gpuDetail}; counter status utilization {gpuDiagnostics.UtilizationCounterStatus}, dedicated memory {gpuDiagnostics.DedicatedMemoryCounterStatus}, shared memory {gpuDiagnostics.SharedMemoryCounterStatus}; target processes {gpuDiagnostics.TargetProcessCount}; sampled {gpuDiagnostics.SampledProcessCount}; engine instances {gpuDiagnostics.EngineCounterInstanceCount}; process-memory instances {gpuDiagnostics.ProcessMemoryCounterInstanceCount}; adapters {gpuDiagnostics.ActiveAdapterCount}; busiest {busiestEngine}; PID-reuse rejected {gpuDiagnostics.PidReuseSamplesRejected}; first-observation counters rejected {gpuDiagnostics.FirstObservationCounterSamplesRejected}; quarantined utilization {gpuDiagnostics.QuarantinedUtilizationSamples}, dedicated memory {gpuDiagnostics.QuarantinedDedicatedMemorySamples}, shared memory {gpuDiagnostics.QuarantinedSharedMemorySamples}; inaccessible target samples {gpuDiagnostics.InaccessibleProcessSamples}; descendant counters not attributed {gpuDiagnostics.UnattributedDescendantCounterInstances}; outside application set {gpuDiagnostics.OutsideApplicationSetCounterInstances}; exited process counters {gpuDiagnostics.ExitedProcessCounterInstances}; unknown PID counters {gpuDiagnostics.UnknownProcessCounterInstances}; malformed instances {gpuDiagnostics.MalformedCounterInstances}; duplicate instances {gpuDiagnostics.DuplicateCounterInstances}; invalid values {gpuDiagnostics.InvalidCounterSamples}; query recreations {gpuDiagnostics.QueryRecreationCount}; collection {gpuDiagnostics.CollectionDurationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms. Dedicated/shared values are sums of Windows per-process counters; shared values are not unique physical ownership and cross-process allocations can be counted more than once.";
        ProcessSummary = $"{snapshot.ProcessCount} process{(snapshot.ProcessCount == 1 ? string.Empty : "es")} · {snapshot.Application.Disposition}";
        ClassificationReason = snapshot.Application.ClassificationReason;
        ClassificationConfidence = $"{snapshot.Application.Confidence} confidence";
        SynchronizeCpuSamples(CpuHistorySeries.Create(history));
        SynchronizeMemorySamples(CpuHistorySeries.CreateMemory(history));
        UpdateMetricExplanations(_metricExplanations.Create(snapshot, history.Count));
        TryLoadIcon(snapshot);
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
                executablePath, 32, CancellationToken.None);
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
            // Icon loading is best-effort; failures are silently ignored.
        }
    }

    public void Relocalize()
    {
        if (_lastSnapshot is not null)
        {
            Update(_lastSnapshot, _lastHistory);
        }
    }

    private ApplicationMetricSnapshot? _lastSnapshot;
    private IReadOnlyList<ApplicationHistoryPoint> _lastHistory = [];

    private void SynchronizeCpuSamples(IReadOnlyList<CpuHistorySample> desired)
    {
        DateTimeOffset firstTimestamp = desired.Count == 0 ? default : desired[0].Timestamp;
        while (CpuSamples.Count > 0
            && (desired.Count == 0 || CpuSamples[0].Timestamp < firstTimestamp))
        {
            CpuSamples.RemoveAt(0);
        }

        for (int index = 0; index < desired.Count; index++)
        {
            CpuHistorySample sample = desired[index];
            if (index < CpuSamples.Count && CpuSamples[index].Timestamp != sample.Timestamp)
            {
                while (CpuSamples.Count > index)
                {
                    CpuSamples.RemoveAt(CpuSamples.Count - 1);
                }
            }

            if (index == CpuSamples.Count)
            {
                CpuSamples.Add(sample);
            }
            else if (CpuSamples[index].Value != sample.Value)
            {
                CpuSamples[index] = sample;
            }
        }

        while (CpuSamples.Count > desired.Count)
        {
            CpuSamples.RemoveAt(CpuSamples.Count - 1);
        }
    }

    private void SynchronizeMemorySamples(IReadOnlyList<CpuHistorySample> desired)
    {
        DateTimeOffset firstTimestamp = desired.Count == 0 ? default : desired[0].Timestamp;
        while (MemorySamples.Count > 0
            && (desired.Count == 0 || MemorySamples[0].Timestamp < firstTimestamp))
        {
            MemorySamples.RemoveAt(0);
        }

        for (int index = 0; index < desired.Count; index++)
        {
            CpuHistorySample sample = desired[index];
            if (index < MemorySamples.Count && MemorySamples[index].Timestamp != sample.Timestamp)
            {
                while (MemorySamples.Count > index)
                {
                    MemorySamples.RemoveAt(MemorySamples.Count - 1);
                }
            }

            if (index == MemorySamples.Count)
            {
                MemorySamples.Add(sample);
            }
            else if (MemorySamples[index].Value != sample.Value)
            {
                MemorySamples[index] = sample;
            }
        }

        while (MemorySamples.Count > desired.Count)
        {
            MemorySamples.RemoveAt(MemorySamples.Count - 1);
        }
    }

    public void Dispose() => ProcessActions?.Dispose();

    /// <summary>
    /// Updates metric explanations in place to preserve expansion state and avoid
    /// scroll jumps. Only recreates the collection when the count changes (e.g.,
    /// language switch or first load).
    /// </summary>
    private void UpdateMetricExplanations(IReadOnlyList<MetricExplanationItem> newItems)
    {
        var current = MetricExplanations;
        if (current.Count != newItems.Count)
        {
            // Structure changed (first load or language change), recreate collection
            var newList = new List<MetricExplanationViewModel>(newItems.Count);
            foreach (var item in newItems)
            {
                newList.Add(new MetricExplanationViewModel(item));
            }
            MetricExplanations = newList;
            return;
        }

        // Update existing items in place to preserve expansion state
        for (int i = 0; i < current.Count; i++)
        {
            current[i].Update(newItems[i]);
        }
    }

    private static string FormatMemory(MetricValue<long> metric) => metric.IsAvailable
        ? $"{PartialPrefix(metric)}{(metric.Value!.Value / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture)} MB"
        : "Unavailable";

    private string FormatPercent(MetricValue<double> metric) => metric.IsAvailable
        ? $"{PartialPrefix(metric)}{metric.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
        : FormatAvailability(metric);

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

    private string FormatBytes(MetricValue<ulong> metric)
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

    private string FormatCount(MetricValue<ulong> metric) => metric.IsAvailable
        ? PartialPrefix(metric) + metric.Value!.Value.ToString("N0", CultureInfo.InvariantCulture)
        : FormatAvailability(metric);

    private string FormatCount(MetricValue<int> metric) => metric.IsAvailable
        ? PartialPrefix(metric) + metric.Value!.Value.ToString("N0", CultureInfo.InvariantCulture)
        : FormatAvailability(metric);

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

    private string FormatIoStatus(MetricValue<double> read, MetricValue<double> write)
    {
        string readStatus = FormatAvailability(read);
        string writeStatus = FormatAvailability(write);
        return string.Equals(readStatus, writeStatus, StringComparison.Ordinal)
            ? readStatus
            : $"{readStatus} / {writeStatus}";
    }

    private static string PartialPrefix<T>(MetricValue<T> metric) where T : struct =>
        metric.IsComplete ? string.Empty : "≥ ";
}
