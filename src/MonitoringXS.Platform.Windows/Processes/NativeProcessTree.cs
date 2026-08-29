using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitoringXS.Platform.Windows.Processes;

internal static class NativeProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;

    public static IReadOnlyList<ProcessEntry> Snapshot()
    {
        using SafeSnapshotHandle snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Process enumeration could not start.");
        }

        ProcessEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        List<ProcessEntry> processes = [];

        if (!Process32First(snapshot, ref entry))
        {
            const int errorNoMoreFiles = 18;
            int error = Marshal.GetLastPInvokeError();
            return error == errorNoMoreFiles
                ? processes
                : throw new Win32Exception(error, "Process enumeration could not read its first entry.");
        }

        do
        {
            if (entry.ProcessId <= int.MaxValue && !string.IsNullOrWhiteSpace(entry.ExecutableFile))
            {
                int parentId = entry.ParentProcessId <= int.MaxValue
                    ? (int)entry.ParentProcessId
                    : 0;
                processes.Add(new ProcessEntry(
                    (int)entry.ProcessId,
                    parentId == 0 ? null : parentId,
                    entry.ExecutableFile,
                    entry.Threads > int.MaxValue ? int.MaxValue : (int)entry.Threads));
            }

            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snapshot, ref entry));

        const int noMoreFiles = 18;
        int finalError = Marshal.GetLastPInvokeError();
        if (finalError != noMoreFiles)
        {
            throw new Win32Exception(finalError, "Process enumeration ended unexpectedly.");
        }

        return processes;
    }

    internal sealed record ProcessEntry(
        int ProcessId,
        int? ParentProcessId,
        string ExecutableName,
        int ThreadCount = 0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeSnapshotHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
