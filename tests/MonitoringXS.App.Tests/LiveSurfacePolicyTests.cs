using MonitoringXS.App.ViewModels;

namespace MonitoringXS.App.Tests;

public sealed class LiveSurfacePolicyTests
{
    [Theory]
    [InlineData("running", WindowPresence.Visible, LiveSurfaces.RunningApplications)]
    [InlineData(null, WindowPresence.Visible, LiveSurfaces.RunningApplications)]
    [InlineData("system-overview", WindowPresence.Visible, LiveSurfaces.SystemOverview)]
    [InlineData("history", WindowPresence.Visible, LiveSurfaces.None)]
    [InlineData("diagnostics", WindowPresence.Visible, LiveSurfaces.None)]
    [InlineData("settings", WindowPresence.Visible, LiveSurfaces.None)]
    [InlineData("about", WindowPresence.Visible, LiveSurfaces.None)]
    [InlineData("running", WindowPresence.Minimized, LiveSurfaces.None)]
    [InlineData("system-overview", WindowPresence.Hidden, LiveSurfaces.None)]
    public void OnlyTheOnScreenPageIsLive(string? tag, WindowPresence presence, LiveSurfaces expected) =>
        Assert.Equal(expected, LiveSurfacePolicy.Visible(tag, presence));

    [Fact]
    public void SkippedSurfacesStayStaleUntilTheyBecomeLive()
    {
        LiveSurfaces stale = LiveSurfacePolicy.MarkSkipped(LiveSurfaces.None, LiveSurfaces.RunningApplications);
        Assert.Equal(LiveSurfaces.SystemOverview, stale);

        stale = LiveSurfacePolicy.MarkSkipped(stale, LiveSurfaces.None);
        Assert.Equal(LiveSurfaces.All, stale);

        Assert.Equal(
            LiveSurfaces.SystemOverview,
            LiveSurfacePolicy.Resumed(stale, LiveSurfaces.SystemOverview));
        Assert.Equal(
            LiveSurfaces.None,
            LiveSurfacePolicy.Resumed(LiveSurfaces.SystemOverview, LiveSurfaces.RunningApplications));
        Assert.Equal(
            LiveSurfaces.None,
            LiveSurfacePolicy.MarkSkipped(LiveSurfaces.None, LiveSurfaces.All));
    }

    [Theory]
    [InlineData(WindowPresence.Visible, false)]
    [InlineData(WindowPresence.Minimized, true)]
    [InlineData(WindowPresence.Hidden, true)]
    public void SamplingSlowsOnlyWhenTheWindowIsNotShown(WindowPresence presence, bool expected) =>
        Assert.Equal(expected, LiveSurfacePolicy.UsesBackgroundCadence(presence));

    [Theory]
    [InlineData(WindowPresence.Visible, WindowPresence.Minimized, true)]
    [InlineData(WindowPresence.Visible, WindowPresence.Hidden, true)]
    [InlineData(WindowPresence.Minimized, WindowPresence.Hidden, false)]
    [InlineData(WindowPresence.Minimized, WindowPresence.Minimized, false)]
    [InlineData(WindowPresence.Minimized, WindowPresence.Visible, false)]
    [InlineData(WindowPresence.Visible, WindowPresence.Visible, false)]
    public void MemoryIsReleasedOnceWhenTheWindowLeavesTheScreen(
        WindowPresence previous,
        WindowPresence current,
        bool expected) =>
        Assert.Equal(expected, LiveSurfacePolicy.ShouldReleaseMemory(previous, current));
}
