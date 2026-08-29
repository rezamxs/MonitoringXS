using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MonitoringXS.ProcessActionTestHelper;

internal static class Program
{
    private static void Main(string[] args)
    {
        int childCount = args.Length == 2
            && args[0] == "--children"
            && int.TryParse(args[1], out int parsed)
                ? Math.Clamp(parsed, 0, 8)
                : 0;
        List<Process> children = [];

        for (int index = 0; index < childCount; index++)
        {
            ProcessStartInfo start = new(Environment.ProcessPath!)
            {
                UseShellExecute = false
            };
            start.ArgumentList.Add("--child");
            children.Add(Process.Start(start)!);
        }

        Console.WriteLine(string.Join(',', children.Select(child => child.Id)));
        Console.Out.Flush();
        NativeWindow.Run();
    }
}

internal static class NativeWindow
{
    private const uint WindowStyle = 0x00CF0000;
    private const uint DestroyMessage = 0x0002;
    private const int ShowNormal = 5;
    private const int UseDefault = unchecked((int)0x80000000);
    private const int ArrowCursor = 32512;
    private const string ClassName = "MonitoringXS.ProcessActionTestHelper.Window";
    private static readonly WindowProcedure Procedure = HandleMessage;

    public static void Run()
    {
        nint instance = GetModuleHandle(null);
        WindowClass windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = instance,
            Cursor = LoadCursor(0, ArrowCursor),
            ClassName = ClassName
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException("Test helper window class registration failed.");
        }

        nint window = CreateWindowEx(
            0,
            ClassName,
            "MonitoringXS Process Action Test Helper",
            WindowStyle,
            UseDefault,
            UseDefault,
            520,
            180,
            0,
            0,
            instance,
            0);
        if (window == 0)
        {
            throw new InvalidOperationException("Test helper window creation failed.");
        }

        ShowWindow(window, ShowNormal);
        UpdateWindow(window);
        while (GetMessage(out Message message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private static nint HandleMessage(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter)
    {
        if (message == DestroyMessage)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, wordParameter, longParameter);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nuint WordParameter;
        public nint LongParameter;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(
        out Message message,
        nint window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, int cursorName);
}
