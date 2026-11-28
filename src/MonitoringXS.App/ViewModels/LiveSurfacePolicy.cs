namespace MonitoringXS.App.ViewModels;

/// <summary>The parts of the main window that present live snapshot data.</summary>
[Flags]
public enum LiveSurfaces
{
    None = 0,
    RunningApplications = 1,
    SystemOverview = 2,
    All = RunningApplications | SystemOverview
}

/// <summary>How the main window is currently shown to the user.</summary>
public enum WindowPresence
{
    Visible,
    Minimized,
    Hidden
}

/// <summary>
/// Pure decisions for presenting live snapshots only where the user can see them.
/// Off-screen surfaces skip per-tick UI work and catch up from the latest
/// snapshot the moment they come back on screen.
/// </summary>
public static class LiveSurfacePolicy
{
    /// <summary>The surfaces that should be updated for the selected page and window presence.</summary>
    public static LiveSurfaces Visible(string? navigationTag, WindowPresence presence)
    {
        if (presence != WindowPresence.Visible)
        {
            return LiveSurfaces.None;
        }

        return navigationTag switch
        {
            "running" or null or "" => LiveSurfaces.RunningApplications,
            "system-overview" => LiveSurfaces.SystemOverview,
            _ => LiveSurfaces.None
        };
    }

    /// <summary>Surfaces that skipped a snapshot and now hold stale data.</summary>
    public static LiveSurfaces MarkSkipped(LiveSurfaces stale, LiveSurfaces live) =>
        stale | (LiveSurfaces.All & ~live);

    /// <summary>Stale surfaces that just became live and must catch up immediately.</summary>
    public static LiveSurfaces Resumed(LiveSurfaces stale, LiveSurfaces live) => stale & live;

    /// <summary>Sampling slows down only while nothing in the window is on screen.</summary>
    public static bool UsesBackgroundCadence(WindowPresence presence) =>
        presence != WindowPresence.Visible;

    /// <summary>Release memory once, on the transition from shown to minimized or hidden.</summary>
    public static bool ShouldReleaseMemory(WindowPresence previous, WindowPresence current) =>
        previous == WindowPresence.Visible && current != WindowPresence.Visible;
}
