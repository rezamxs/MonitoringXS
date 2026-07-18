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
}
