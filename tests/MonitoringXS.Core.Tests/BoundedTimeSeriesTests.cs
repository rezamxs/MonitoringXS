using MonitoringXS.Core.Collections;

namespace MonitoringXS.Core.Tests;

public sealed class BoundedTimeSeriesTests
{
    [Fact]
    public void AddEvictsOldestItemAtCapacity()
    {
        BoundedTimeSeries<int> series = new(2);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        series.Add(now, 1);
        series.Add(now.AddSeconds(1), 2);
        series.Add(now.AddSeconds(2), 3);

        Assert.Equal([2, 3], series.Snapshot().Select(item => item.Value));
    }

    [Fact]
    public void ValuesReturnsOnlyValuesInOrder()
    {
        BoundedTimeSeries<int> series = new(2);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        series.Add(now, 1);
        series.Add(now.AddSeconds(1), 2);
        series.Add(now.AddSeconds(2), 3);

        Assert.Equal([2, 3], series.Values());
    }

    [Fact]
    public void TimeWindowKeepsOnlyTheLastWindowAtSlowerCadence()
    {
        BoundedTimeSeries<int> series = new(60, TimeSpan.FromMinutes(1));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        for (int index = 0; index <= 60; index++)
        {
            series.Add(start.AddSeconds(index * 2), index);
        }

        Assert.Equal(30, series.Count);
        Assert.Equal(31, series.Values()[0]);
        Assert.Equal(60, series.Values()[^1]);
    }

    [Fact]
    public void TimeWindowStillHonorsCapacityAtFastCadence()
    {
        BoundedTimeSeries<int> series = new(60, TimeSpan.FromMinutes(1));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        for (int index = 0; index < 200; index++)
        {
            series.Add(start.AddMilliseconds(index * 100), index);
        }

        Assert.Equal(60, series.Count);
        Assert.Equal(199, series.Values()[^1]);
    }

    [Fact]
    public void TimeWindowAlwaysKeepsTheNewestPointAfterALongPause()
    {
        BoundedTimeSeries<int> series = new(60, TimeSpan.FromMinutes(1));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        series.Add(start, 1);
        series.Add(start.AddMinutes(10), 2);

        Assert.Equal([2], series.Values());
    }

    [Fact]
    public void NonPositiveWindowIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedTimeSeries<int>(1, TimeSpan.Zero));
}
