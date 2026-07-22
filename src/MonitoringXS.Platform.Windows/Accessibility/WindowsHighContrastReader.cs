using System.Runtime.InteropServices;

namespace MonitoringXS.Platform.Windows.Accessibility;

public static partial class WindowsHighContrastReader
{
    private const uint GetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;

    public static bool IsEnabled()
    {
        HighContrast settings = new()
        {
            Size = (uint)Marshal.SizeOf<HighContrast>()
        };

        return SystemParametersInfo(GetHighContrast, settings.Size, ref settings, 0) != 0 &&
            (settings.Flags & HighContrastOn) != 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static partial int SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrast settings,
        uint update);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }
}
