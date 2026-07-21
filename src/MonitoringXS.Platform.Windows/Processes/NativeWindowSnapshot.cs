using System.Runtime.InteropServices;

namespace MonitoringXS.Platform.Windows.Processes;

internal static class NativeWindowSnapshot
{
    private const uint GetWindowOwner = 4;
    private const int ExtendedStyleIndex = -20;
    private const int ToolWindowStyle = 0x00000080;
    private const uint DwmWindowAttributeCloaked = 14;

    public static IReadOnlyDictionary<int, WindowDescriptor> Capture()
    {
        Dictionary<int, WindowDescriptor> windows = [];
        EnumWindows((window, parameter) =>
        {
            if (!IsCandidate(window))
            {
                return true;
            }

            uint threadId = GetWindowThreadProcessId(window, out uint processId);
            if (threadId == 0 || processId == 0 || processId > int.MaxValue)
            {
                return true;
            }

            string? title = GetTitle(window);
            int id = (int)processId;
            if (!windows.TryGetValue(id, out WindowDescriptor? existing)
                || existing.Title is null && title is not null)
            {
                windows[id] = new WindowDescriptor(window, title);
            }

            return true;
        }, nint.Zero);
        return windows;
    }

    private static bool IsCandidate(nint window)
    {
        if (!IsWindowVisible(window)
            || GetWindow(window, GetWindowOwner) != nint.Zero
            || (GetWindowLong(window, ExtendedStyleIndex) & ToolWindowStyle) != 0)
        {
            return false;
        }

        int cloaked = 0;
        int result = DwmGetWindowAttribute(
            window,
            DwmWindowAttributeCloaked,
            ref cloaked,
            (uint)Marshal.SizeOf<int>());
        return result != 0 || cloaked == 0;
    }

    private static unsafe string? GetTitle(nint window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        char[] title = new char[Math.Min(length + 1, 1024)];
        fixed (char* buffer = title)
        {
            int copied = GetWindowText(window, buffer, title.Length);
            return copied > 0 ? NullIfWhitespace(new string(buffer, 0, copied)) : null;
        }
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record WindowDescriptor(nint Handle, string? Title);

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetWindowText(nint window, char* text, int maximumCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        ref int value,
        uint valueSize);
}
