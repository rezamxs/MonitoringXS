namespace MonitoringXS.App;

/// <summary>
/// One icon per metric, shared by System Overview and the Running Apps detail
/// view. Every codepoint exists in both Segoe Fluent Icons (Windows 11) and
/// Segoe MDL2 Assets (Windows 10), so the glyph never falls back to a box.
/// </summary>
public static class MetricGlyphs
{
    /// <summary>Component (processor chip).</summary>
    public const string Cpu = "\uE950";

    /// <summary>SmartcardVirtual (memory module).</summary>
    public const string Memory = "\uE964";

    /// <summary>HardDrive.</summary>
    public const string Disk = "\uEDA2";

    /// <summary>Globe (network traffic).</summary>
    public const string Network = "\uE774";

    /// <summary>TVMonitor (display adapter).</summary>
    public const string Gpu = "\uE7F4";
}
