using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Metrics;

public sealed class WindowsProcessIoCounterReader : IProcessIoCounterReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;

    public MetricValue<ProcessIoCounters> Read(ProcessInstanceId process)
    {
        using SafeProcessHandle handle = OpenProcess(ProcessQueryLimitedInformation, false, process.ProcessId);
        if (handle.IsInvalid)
        {
            return Failure(Marshal.GetLastPInvokeError(), "The process could not be opened for I/O sampling.");
        }

        if (!GetProcessTimes(handle, out FileTime creation, out _, out _, out _))
        {
            return Failure(Marshal.GetLastPInvokeError(), "The process start time could not be verified.");
        }

        DateTimeOffset observedStart = DateTimeOffset.FromFileTime(creation.ToInt64()).ToUniversalTime();
        if (observedStart != process.StartTimeUtc)
        {
            return MetricValue<ProcessIoCounters>.Unavailable(
                MetricAvailability.Error,
                "Process ID was reused before I/O sampling completed.");
        }

        if (!GetProcessIoCounters(handle, out IoCounters counters))
        {
            return Failure(Marshal.GetLastPInvokeError(), "Windows did not return process I/O counters.");
        }

        return MetricValue<ProcessIoCounters>.Available(new ProcessIoCounters(
            counters.ReadOperationCount,
            counters.WriteOperationCount,
            counters.OtherOperationCount,
            counters.ReadTransferCount,
            counters.WriteTransferCount,
            counters.OtherTransferCount));
    }

    private static MetricValue<ProcessIoCounters> Failure(int errorCode, string detail) =>
        MetricValue<ProcessIoCounters>.Unavailable(
            errorCode == ErrorAccessDenied ? MetricAvailability.AccessDenied : MetricAvailability.Error,
            $"{detail} Win32 error {errorCode}.");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

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

        public long ToInt64() => unchecked((long)(((ulong)_highDateTime << 32) | _lowDateTime));
    }
}
