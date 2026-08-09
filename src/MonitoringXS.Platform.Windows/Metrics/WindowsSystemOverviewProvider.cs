using System.Diagnostics;
using System.Runtime.InteropServices;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Metrics;

/// <summary>
/// Provides system-wide CPU and memory metrics using Win32 APIs.
/// CPU uses GetSystemTimes with delta semantics (first sample = WarmingUp).
/// Memory uses GlobalMemoryStatusEx for instantaneous physical memory state.
/// Disk, network, and GPU are reported as Unsupported; the SystemOverviewService
/// merges those from existing ETW/PDH collectors.
/// </summary>
public sealed class WindowsSystemOverviewProvider : ISystemOverviewProvider
{
    private readonly object _gate = new();
    private CpuBaseline? _cpuBaseline;

    public ValueTask<SystemOverviewSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();

        MetricValue<double> cpu;
        lock (_gate)
        {
            cpu = CaptureCpu();
        }

        (MetricValue<long> totalMemory,
         MetricValue<long> usedMemory,
         MetricValue<long> availableMemory,
         MetricValue<double> memoryPercent) = CaptureMemory();

        stopwatch.Stop();

        MetricValue<double> unsupported = MetricValue<double>.Unavailable(
            MetricAvailability.Unsupported,
            "System-wide value is provided by the SystemOverviewService from existing collectors.");

        SystemOverviewDiagnostics diagnostics = new(
            cpu.Availability,
            cpu.Detail,
            totalMemory.Availability,
            totalMemory.Detail,
            MetricAvailability.Unsupported,
            "Provided by existing physical-disk ETW collector.",
            MetricAvailability.Unsupported,
            "Provided by existing network ETW collector.",
            MetricAvailability.Unsupported,
            "Provided by existing GPU PDH collector.",
            (int)stopwatch.Elapsed.TotalMilliseconds,
            cpu.IsAvailable && totalMemory.IsAvailable);

        return ValueTask.FromResult(new SystemOverviewSnapshot(
            DateTimeOffset.UtcNow,
            cpu,
            totalMemory,
            usedMemory,
            availableMemory,
            memoryPercent,
            unsupported,
            unsupported,
            unsupported,
            unsupported,
            unsupported,
            diagnostics));
    }

    /// <summary>
    /// Resets the CPU baseline so the next capture returns WarmingUp.
    /// Used for testing and provider restart scenarios.
    /// </summary>
    internal void ResetCpuBaseline()
    {
        lock (_gate)
        {
            _cpuBaseline = null;
        }
    }

    private MetricValue<double> CaptureCpu()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            int error = Marshal.GetLastPInvokeError();
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                $"GetSystemTimes failed with Win32 error {error}.");
        }

        ulong idleTicks = idle.ToUInt64();
        ulong kernelTicks = kernel.ToUInt64();
        ulong userTicks = user.ToUInt64();

        // Total CPU time = kernel + user (kernel includes idle on some systems,
        // but the standard formula is: utilization = 1 - idle/(kernel+user)).
        // We use the delta approach: totalDelta = (kernel+user) - prev(kernel+user),
        // idleDelta = idle - prev(idle), utilization = (totalDelta - idleDelta) / totalDelta.
        ulong totalTicks = SaturatingAdd(kernelTicks, userTicks);
        CpuBaseline current = new(idleTicks, totalTicks);

        if (_cpuBaseline is not { } previous)
        {
            _cpuBaseline = current;
            return MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second system CPU sample is required for a delta calculation.");
        }

        _cpuBaseline = current;

        ulong totalDelta = SubtractSaturating(current.TotalTicks, previous.TotalTicks);
        ulong idleDelta = SubtractSaturating(current.IdleTicks, previous.IdleTicks);

        if (totalDelta == 0)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "System CPU sampling interval produced zero elapsed ticks.");
        }

        // Guard against counter anomalies where idle delta exceeds total delta.
        if (idleDelta > totalDelta)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "System CPU counters produced an inconsistent delta.");
        }

        double utilization = (double)(totalDelta - idleDelta) / totalDelta * 100d;
        utilization = Math.Clamp(utilization, 0d, 100d);

        return MetricValue<double>.Available(utilization);
    }

    private static (MetricValue<long> Total, MetricValue<long> Used, MetricValue<long> Available, MetricValue<double> Percent)
        CaptureMemory()
    {
        MemoryStatusEx status = new() { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            int error = Marshal.GetLastPInvokeError();
            MetricValue<long> unavailable = MetricValue<long>.Unavailable(
                MetricAvailability.Error,
                $"GlobalMemoryStatusEx failed with Win32 error {error}.");
            MetricValue<double> unavailablePercent = MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                $"GlobalMemoryStatusEx failed with Win32 error {error}.");
            return (unavailable, unavailable, unavailable, unavailablePercent);
        }

        long totalBytes = status.TotalPhys > long.MaxValue ? long.MaxValue : (long)status.TotalPhys;
        long availableBytes = status.AvailPhys > long.MaxValue ? long.MaxValue : (long)status.AvailPhys;
        long usedBytes = Math.Max(0, totalBytes - availableBytes);

        double percent = totalBytes > 0
            ? Math.Clamp((double)usedBytes / totalBytes * 100d, 0d, 100d)
            : 0d;

        return (
            MetricValue<long>.Available(totalBytes),
            MetricValue<long>.Available(usedBytes),
            MetricValue<long>.Available(availableBytes),
            MetricValue<double>.Available(percent));
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static ulong SubtractSaturating(ulong left, ulong right) =>
        left < right ? 0 : left - right;

    private readonly record struct CpuBaseline(ulong IdleTicks, ulong TotalTicks);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public ulong ToUInt64() => ((ulong)_highDateTime << 32) | _lowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}