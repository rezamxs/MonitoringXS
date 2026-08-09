using System.Diagnostics;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Collections;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application;

/// <summary>
/// Coordinates system-wide overview snapshots by merging CPU/memory from
/// <see cref="ISystemOverviewProvider"/> with disk/network/GPU data from
/// existing ETW/PDH collectors. Maintains a bounded one-minute time-series
/// for later UI chart consumption.
/// </summary>
public sealed class SystemOverviewService
{
    private const int OneMinuteCapacity = 60;
    private readonly ISystemOverviewProvider _provider;
    private readonly BoundedTimeSeries<SystemOverviewHistoryPoint> _history = new(OneMinuteCapacity);
    private readonly object _gate = new();

    // Track previous session totals for rate computation.
    private DiskRateState? _previousDisk;
    private NetworkRateState? _previousNetwork;

    public SystemOverviewService(ISystemOverviewProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Captures a system overview snapshot, merging provider CPU/memory with
    /// optional disk/network/GPU diagnostics from existing collectors.
    /// </summary>
    public async ValueTask<SystemOverviewSnapshot> CaptureAsync(
        PhysicalDiskCollectorDiagnostics? diskDiagnostics,
        NetworkCollectorDiagnostics? networkDiagnostics,
        GpuCounterBatch? gpuBatch,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SystemOverviewSnapshot baseSnapshot = await _provider.CaptureAsync(cancellationToken);
        stopwatch.Stop();

        DiskRateState? previousDisk = _previousDisk;
        NetworkRateState? previousNetwork = _previousNetwork;
        MetricValue<double> diskRead = ComputeDiskReadRate(diskDiagnostics, baseSnapshot.CapturedAt, previousDisk);
        MetricValue<double> diskWrite = ComputeDiskWriteRate(diskDiagnostics, baseSnapshot.CapturedAt, previousDisk);
        MetricValue<double> networkReceive = ComputeNetworkReceiveRate(networkDiagnostics, baseSnapshot.CapturedAt, previousNetwork);
        MetricValue<double> networkSend = ComputeNetworkSendRate(networkDiagnostics, baseSnapshot.CapturedAt, previousNetwork);

        // Update state after all rate computations are complete.
        if (diskDiagnostics is { } diskDiag)
        {
            MetricAvailability diskStatus = diskDiag.CollectorStatus ?? MetricAvailability.Unavailable;
            if (diskStatus == MetricAvailability.Available || diskStatus == MetricAvailability.Partial)
            {
                _previousDisk = new DiskRateState(baseSnapshot.CapturedAt, diskDiag.ReadBytesObserved, diskDiag.WriteBytesObserved);
            }
        }
        if (networkDiagnostics is { } netDiag)
        {
            MetricAvailability netStatus = netDiag.CollectorStatus;
            if (netStatus == MetricAvailability.Available || netStatus == MetricAvailability.Partial)
            {
                _previousNetwork = new NetworkRateState(baseSnapshot.CapturedAt, netDiag.TotalSourceReceiveBytes, netDiag.TotalSourceSendBytes);
            }
        }
        MetricValue<double> gpuUtilization = ExtractGpuUtilization(gpuBatch);

        MetricAvailability diskAvailability = CombineAvailability(diskRead.Availability, diskWrite.Availability);
        MetricAvailability networkAvailability = CombineAvailability(networkReceive.Availability, networkSend.Availability);

        SystemOverviewDiagnostics diagnostics = new(
            baseSnapshot.Diagnostics.CpuAvailability,
            baseSnapshot.Diagnostics.CpuDetail,
            baseSnapshot.Diagnostics.MemoryAvailability,
            baseSnapshot.Diagnostics.MemoryDetail,
            diskAvailability,
            diskDiagnostics is null ? "Physical-disk collector is not configured." : null,
            networkAvailability,
            networkDiagnostics is null ? "Network collector is not configured." : null,
            gpuUtilization.Availability,
            gpuUtilization.Detail,
            (int)stopwatch.Elapsed.TotalMilliseconds,
            baseSnapshot.TotalCpuPercent.IsAvailable
                && baseSnapshot.TotalPhysicalMemoryBytes.IsAvailable
                && diskAvailability == MetricAvailability.Available
                && networkAvailability == MetricAvailability.Available
                && gpuUtilization.IsAvailable);

        SystemOverviewSnapshot merged = baseSnapshot with
        {
            DiskReadBytesPerSecond = diskRead,
            DiskWriteBytesPerSecond = diskWrite,
            NetworkReceiveBytesPerSecond = networkReceive,
            NetworkSendBytesPerSecond = networkSend,
            GpuUtilizationPercent = gpuUtilization,
            Diagnostics = diagnostics
        };

        lock (_gate)
        {
            _history.Add(merged.CapturedAt, new SystemOverviewHistoryPoint(
                merged.CapturedAt,
                merged.TotalCpuPercent.IsAvailable ? merged.TotalCpuPercent.Value : null,
                merged.PhysicalMemoryUtilizationPercent.IsAvailable ? merged.PhysicalMemoryUtilizationPercent.Value : null,
                merged.DiskReadBytesPerSecond.IsAvailable ? merged.DiskReadBytesPerSecond.Value : null,
                merged.DiskWriteBytesPerSecond.IsAvailable ? merged.DiskWriteBytesPerSecond.Value : null,
                merged.NetworkReceiveBytesPerSecond.IsAvailable ? merged.NetworkReceiveBytesPerSecond.Value : null,
                merged.NetworkSendBytesPerSecond.IsAvailable ? merged.NetworkSendBytesPerSecond.Value : null,
                merged.GpuUtilizationPercent.IsAvailable ? merged.GpuUtilizationPercent.Value : null));
        }

        return merged;
    }

    /// <summary>
    /// Returns a snapshot of the bounded one-minute history.
    /// </summary>
    public IReadOnlyList<SystemOverviewHistoryPoint> GetHistory()
    {
        lock (_gate)
        {
            return _history.Snapshot().Select(item => item.Value).ToArray();
        }
    }

    private static MetricValue<double> ComputeDiskReadRate(
        PhysicalDiskCollectorDiagnostics? diagnostics,
        DateTimeOffset capturedAt,
        DiskRateState? previousDisk)
    {
        if (diagnostics is null)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Unsupported,
                "Physical-disk collector is not configured.");
        }

        MetricAvailability status = diagnostics.Value.CollectorStatus ?? MetricAvailability.Unavailable;
        if (status != MetricAvailability.Available && status != MetricAvailability.Partial)
        {
            return MetricValue<double>.Unavailable(status, "Physical-disk collector is not producing data.");
        }

        ulong currentReadBytes = diagnostics.Value.ReadBytesObserved;
        if (previousDisk is not { } previous)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second disk sample is required for a rate calculation.");
        }

        double elapsedSeconds = (capturedAt - previous.CapturedAt).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "Invalid disk sampling interval.");
        }

        ulong readDelta = SubtractSaturating(currentReadBytes, previous.ReadBytes);
        double rate = readDelta / elapsedSeconds;

        bool isPartial = status == MetricAvailability.Partial || diagnostics.Value.SessionTotalsAreLowerBounds;
        return isPartial
            ? MetricValue<double>.Partial(rate, "Disk read rate is a lower bound due to incomplete session data.")
            : MetricValue<double>.Available(rate);
    }

    private static MetricValue<double> ComputeDiskWriteRate(
        PhysicalDiskCollectorDiagnostics? diagnostics,
        DateTimeOffset capturedAt,
        DiskRateState? previousDisk)
    {
        if (diagnostics is null)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Unsupported,
                "Physical-disk collector is not configured.");
        }

        MetricAvailability status = diagnostics.Value.CollectorStatus ?? MetricAvailability.Unavailable;
        if (status != MetricAvailability.Available && status != MetricAvailability.Partial)
        {
            return MetricValue<double>.Unavailable(status, "Physical-disk collector is not producing data.");
        }

        ulong currentWriteBytes = diagnostics.Value.WriteBytesObserved;
        if (previousDisk is not { } previous)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second disk sample is required for a rate calculation.");
        }

        double elapsedSeconds = (capturedAt - previous.CapturedAt).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "Invalid disk sampling interval.");
        }

        ulong writeDelta = SubtractSaturating(currentWriteBytes, previous.WriteBytes);
        double rate = writeDelta / elapsedSeconds;

        bool isPartial = status == MetricAvailability.Partial || diagnostics.Value.SessionTotalsAreLowerBounds;
        return isPartial
            ? MetricValue<double>.Partial(rate, "Disk write rate is a lower bound due to incomplete session data.")
            : MetricValue<double>.Available(rate);
    }

    private static MetricValue<double> ComputeNetworkReceiveRate(
        NetworkCollectorDiagnostics? diagnostics,
        DateTimeOffset capturedAt,
        NetworkRateState? previousNetwork)
    {
        if (diagnostics is null)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Unsupported,
                "Network collector is not configured.");
        }

        MetricAvailability status = diagnostics.Value.CollectorStatus;
        if (status != MetricAvailability.Available && status != MetricAvailability.Partial)
        {
            return MetricValue<double>.Unavailable(status, diagnostics.Value.CollectorStatusReason);
        }

        ulong currentReceiveBytes = diagnostics.Value.TotalSourceReceiveBytes;
        if (previousNetwork is not { } previous)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second network sample is required for a rate calculation.");
        }

        double elapsedSeconds = (capturedAt - previous.CapturedAt).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "Invalid network sampling interval.");
        }

        ulong receiveDelta = SubtractSaturating(currentReceiveBytes, previous.ReceiveBytes);
        double rate = receiveDelta / elapsedSeconds;

        bool isPartial = status == MetricAvailability.Partial || diagnostics.Value.SessionTotalsAreLowerBounds;
        return isPartial
            ? MetricValue<double>.Partial(rate, "Network receive rate is a lower bound due to incomplete session data.")
            : MetricValue<double>.Available(rate);
    }

    private static MetricValue<double> ComputeNetworkSendRate(
        NetworkCollectorDiagnostics? diagnostics,
        DateTimeOffset capturedAt,
        NetworkRateState? previousNetwork)
    {
        if (diagnostics is null)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Unsupported,
                "Network collector is not configured.");
        }

        MetricAvailability status = diagnostics.Value.CollectorStatus;
        if (status != MetricAvailability.Available && status != MetricAvailability.Partial)
        {
            return MetricValue<double>.Unavailable(status, diagnostics.Value.CollectorStatusReason);
        }

        ulong currentSendBytes = diagnostics.Value.TotalSourceSendBytes;
        if (previousNetwork is not { } previous)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second network sample is required for a rate calculation.");
        }

        double elapsedSeconds = (capturedAt - previous.CapturedAt).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "Invalid network sampling interval.");
        }

        ulong sendDelta = SubtractSaturating(currentSendBytes, previous.SendBytes);
        double rate = sendDelta / elapsedSeconds;

        bool isPartial = status == MetricAvailability.Partial || diagnostics.Value.SessionTotalsAreLowerBounds;
        return isPartial
            ? MetricValue<double>.Partial(rate, "Network send rate is a lower bound due to incomplete session data.")
            : MetricValue<double>.Available(rate);
    }

    private static MetricValue<double> ExtractGpuUtilization(GpuCounterBatch? batch)
    {
        if (batch is null)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Unsupported,
                "GPU counter source is not configured.");
        }

        if (batch.Availability == MetricAvailability.WarmingUp)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                batch.Diagnostics.CollectorStatusReason ?? "GPU counters require a second sample.");
        }

        if (batch.Availability != MetricAvailability.Available && batch.Availability != MetricAvailability.Partial)
        {
            return MetricValue<double>.Unavailable(
                batch.Availability,
                batch.Diagnostics.CollectorStatusReason ?? "GPU counters are unavailable.");
        }

        // Use machine-wide GPU utilization when available. This represents the busiest
        // single engine across ALL processes on the system, not just monitored applications.
        if (batch.MachineWideGpuUtilizationPercent is { } machineWide
            && machineWide.Availability == MetricAvailability.Available
            && machineWide.Value is double mwValue
            && double.IsFinite(mwValue))
        {
            if (mwValue >= 0d && mwValue <= 100d)
            {
                return batch.Availability == MetricAvailability.Partial
                    ? MetricValue<double>.Partial(
                        mwValue,
                        "GPU utilization is partial; some engines or processes were unavailable.")
                    : MetricValue<double>.Available(mwValue);
            }

            // Machine-wide value exists but is impossible; reject it.
            if (mwValue > 100d)
            {
                return MetricValue<double>.Unavailable(
                    MetricAvailability.Error,
                    $"GPU engine reported an impossible utilization of {mwValue:F1}%; the value was rejected.");
            }
        }

        // Fallback: no machine-wide reading available (e.g., counter source did not
        // produce one). Do NOT fabricate a value from monitored-app absence.
        return MetricValue<double>.Unavailable(
            MetricAvailability.Unavailable,
            "No machine-wide GPU engine utilization data was observed in this capture.");
    }

    private static MetricAvailability CombineAvailability(MetricAvailability first, MetricAvailability second)
    {
        if (first == MetricAvailability.Available && second == MetricAvailability.Available)
        {
            return MetricAvailability.Available;
        }

        if (first == MetricAvailability.Available || second == MetricAvailability.Available
            || first == MetricAvailability.Partial || second == MetricAvailability.Partial)
        {
            return MetricAvailability.Partial;
        }

        return first;
    }

    private static ulong SubtractSaturating(ulong left, ulong right) =>
        left < right ? 0 : left - right;

    private readonly record struct DiskRateState(DateTimeOffset CapturedAt, ulong ReadBytes, ulong WriteBytes);

    private readonly record struct NetworkRateState(DateTimeOffset CapturedAt, ulong ReceiveBytes, ulong SendBytes);
}

/// <summary>
/// A single point in the system overview one-minute history.
/// Null values indicate the metric was unavailable at that capture time.
/// </summary>
public sealed record SystemOverviewHistoryPoint(
    DateTimeOffset Timestamp,
    double? CpuPercent,
    double? MemoryUtilizationPercent,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond,
    double? NetworkReceiveBytesPerSecond,
    double? NetworkSendBytesPerSecond,
    double? GpuUtilizationPercent);