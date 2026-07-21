using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Metrics;

public sealed class WindowsProcessResourceCounterReader : IProcessResourceCounterReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;

    public MetricValue<ProcessResourceCounters> Read(ProcessInstanceId process)
    {
        using SafeProcessHandle handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            process.ProcessId);
        if (handle.IsInvalid)
        {
            return Failure(Marshal.GetLastPInvokeError(), "The process could not be opened for resource sampling.");
        }

        if (!GetProcessTimes(handle, out FileTime creation, out _, out FileTime kernel, out FileTime user))
        {
            return Failure(Marshal.GetLastPInvokeError(), "The process times could not be read.");
        }

        DateTimeOffset observedStart;
        try
        {
            observedStart = DateTimeOffset.FromFileTime(creation.ToInt64());
        }
        catch (ArgumentOutOfRangeException)
        {
            return MetricValue<ProcessResourceCounters>.Unavailable(
                MetricAvailability.Error,
                "The process start time returned by Windows was invalid.");
        }

        if (observedStart != process.StartTimeUtc)
        {
            return MetricValue<ProcessResourceCounters>.Unavailable(
                MetricAvailability.Error,
                "Process ID was reused before resource sampling completed.");
        }

        ProcessMemoryCounters memory = new() { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        if (!K32GetProcessMemoryInfo(handle, ref memory, memory.Size))
        {
            return Failure(Marshal.GetLastPInvokeError(), "The process working set could not be read.");
        }

        MetricValue<ProcessIoCounters> io = ReadIo(handle);
        ulong processorTicks = SaturatingAdd(kernel.ToUInt64(), user.ToUInt64());
        TimeSpan totalProcessorTime = TimeSpan.FromTicks(
            processorTicks > long.MaxValue ? long.MaxValue : (long)processorTicks);
        ulong workingSet = memory.WorkingSetSize;
        return MetricValue<ProcessResourceCounters>.Available(new ProcessResourceCounters(
            totalProcessorTime,
            workingSet > long.MaxValue ? long.MaxValue : (long)workingSet,
            io));
    }

    private static MetricValue<ProcessIoCounters> ReadIo(SafeProcessHandle process)
    {
        if (!GetProcessIoCounters(process, out IoCounters counters))
        {
            int error = Marshal.GetLastPInvokeError();
            return MetricValue<ProcessIoCounters>.Unavailable(
                error == ErrorAccessDenied ? MetricAvailability.AccessDenied : MetricAvailability.Error,
                $"The process I/O counters could not be read. Win32 error {error}.");
        }

        return MetricValue<ProcessIoCounters>.Available(new ProcessIoCounters(
            counters.ReadOperationCount,
            counters.WriteOperationCount,
            counters.OtherOperationCount,
            counters.ReadTransferCount,
            counters.WriteTransferCount,
            counters.OtherTransferCount));
    }

    private static MetricValue<ProcessResourceCounters> Failure(int error, string detail) =>
        MetricValue<ProcessResourceCounters>.Unavailable(
            error == ErrorAccessDenied ? MetricAvailability.AccessDenied : MetricAvailability.Error,
            $"{detail} Win32 error {error}.");

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(
        SafeProcessHandle process,
        ref ProcessMemoryCounters counters,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoCounters
    {
        public readonly ulong ReadOperationCount;
        public readonly ulong WriteOperationCount;
        public readonly ulong OtherOperationCount;
        public readonly ulong ReadTransferCount;
        public readonly ulong WriteTransferCount;
        public readonly ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public ulong ToUInt64() => ((ulong)_highDateTime << 32) | _lowDateTime;

        public long ToInt64() => unchecked((long)ToUInt64());
    }
}
