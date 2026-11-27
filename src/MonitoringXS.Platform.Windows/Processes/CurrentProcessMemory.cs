using System.Runtime;
using System.Runtime.InteropServices;

namespace MonitoringXS.Platform.Windows.Processes;

/// <summary>
/// Gives memory back to Windows while the app has nothing on screen. Called once
/// when the window is minimized or hidden, never per tick.
/// </summary>
public static partial class CurrentProcessMemory
{
    private const nint KeepDefaultWorkingSetSize = -1;

    /// <summary>
    /// Compacts the managed heap, returns its free segments to the OS and asks
    /// Windows to trim the working set. Pages the app touches again fault back in.
    /// </summary>
    public static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _ = SetProcessWorkingSetSize(
            GetCurrentProcess(),
            KeepDefaultWorkingSetSize,
            KeepDefaultWorkingSetSize);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(
        nint process,
        nint minimumWorkingSetSize,
        nint maximumWorkingSetSize);
}
