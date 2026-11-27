using MonitoringXS.Application;

namespace MonitoringXS.Application.Tests;

public sealed class LiveRefreshCadenceTests
{
    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(5, false, 5)]
    [InlineData(1, true, 5)]
    [InlineData(2, true, 5)]
    [InlineData(5, true, 5)]
    [InlineData(10, true, 10)]
    public void EffectiveIntervalSlowsOnlyFasterChoicesInBackground(
        int requestedSeconds,
        bool background,
        int expectedSeconds)
    {
        TimeSpan effective = LiveRefreshCadence.EffectiveInterval(
            TimeSpan.FromSeconds(requestedSeconds),
            background);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), effective);
    }

    [Fact]
    public void BackgroundKeepsTheRequestedIntervalAndRestoresIt()
    {
        LiveRefreshCadence cadence = new(TimeSpan.FromSeconds(1));

        cadence.SetBackground(true);
        Assert.True(cadence.IsBackground);
        Assert.Equal(LiveRefreshCadence.BackgroundInterval, cadence.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1), cadence.RequestedInterval);

        cadence.Update(TimeSpan.FromSeconds(2));
        Assert.Equal(LiveRefreshCadence.BackgroundInterval, cadence.Interval);
        Assert.Equal(TimeSpan.FromSeconds(2), cadence.RequestedInterval);

        cadence.SetBackground(false);
        Assert.False(cadence.IsBackground);
        Assert.Equal(TimeSpan.FromSeconds(2), cadence.Interval);
    }

    [Fact]
    public async Task ReturningToForegroundWakesAWaitingLoopToReplanItsWait()
    {
        LiveRefreshCadence cadence = new(TimeSpan.FromHours(1));
        cadence.SetBackground(true);
        Task wait = cadence.WaitForNextCaptureAsync(TestContext.Current.CancellationToken).AsTask();

        cadence.Update(TimeSpan.FromMilliseconds(1));
        cadence.SetBackground(false);

        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMilliseconds(1), cadence.Interval);
    }

    [Fact]
    public void RepeatedSameStateIsANoOp()
    {
        LiveRefreshCadence cadence = new(TimeSpan.FromSeconds(1));

        cadence.SetBackground(false);
        cadence.SetBackground(false);

        Assert.False(cadence.IsBackground);
        Assert.Equal(TimeSpan.FromSeconds(1), cadence.Interval);
    }
}
