using MonitoringXS.App.ViewModels;

namespace MonitoringXS.App.Tests;

public sealed class ApplicationSortRefreshPolicyTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Baseline = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DelaysMetricDrivenReorderingUntilTheIntervalHasElapsed()
    {
        Assert.False(ApplicationSortRefreshPolicy.IsRefreshDue(
            Baseline,
            Baseline.AddSeconds(4.999),
            Interval,
            force: false));
        Assert.True(ApplicationSortRefreshPolicy.IsRefreshDue(
            Baseline,
            Baseline.AddSeconds(5),
            Interval,
            force: false));
    }

    [Fact]
    public void AppliesUserAndMembershipChangesImmediately()
    {
        Assert.True(ApplicationSortRefreshPolicy.IsRefreshDue(
            Baseline,
            Baseline.AddSeconds(1),
            Interval,
            force: true));
    }

    [Fact]
    public void AppliesTheFirstRefreshAndRecoversFromAClockRollback()
    {
        Assert.True(ApplicationSortRefreshPolicy.IsRefreshDue(
            DateTimeOffset.MinValue,
            Baseline,
            Interval,
            force: false));
        Assert.True(ApplicationSortRefreshPolicy.IsRefreshDue(
            Baseline,
            Baseline.AddSeconds(-1),
            Interval,
            force: false));
    }
}
