using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitoringXS.Platform.Windows.Processes;

internal static class NativeProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;

    public static IReadOnlyDictionary<int, int> SnapshotParents()
    {
        using SafeSnapshotHandle snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            return new Dictionary<int, int>();
        }

        ProcessEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        Dictionary<int, int> parents = [];

        if (!Process32First(snapshot, ref entry))
        {
            return parents;
        }

        do
        {
            parents[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId);
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snapshot, ref entry));

        return parents;
    }

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
