using MonitoringXS.App.Controls;
using MonitoringXS.Application;

namespace MonitoringXS.App.Tests;

public sealed class CpuHistorySeriesTests
{
    [Fact]
    public void CreateOrdersSamplesAndKeepsTheLastDuplicateTimestamp()
    {
        DateTimeOffset start = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        ApplicationHistoryPoint[] history =
        [
            new(start.AddSeconds(2), 20, null),
            new(start, 10, null),
            new(start.AddSeconds(2), 30, null),
            new(start.AddSeconds(1), 15, null)
        ];

        IReadOnlyList<CpuHistorySample> result = CpuHistorySeries.Create(history);

        Assert.Equal([start, start.AddSeconds(1), start.AddSeconds(2)], result.Select(sample => sample.Timestamp));
        Assert.Equal([10d, 15d, 30d], result.Select(sample => sample.Value));
    }

    [Fact]
    public void CreatePreservesUnavailableGapsAndRejectsInvalidNumbers()
    {
        DateTimeOffset start = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        ApplicationHistoryPoint[] history =
        [
            new(start, 10, null),
            new(start.AddSeconds(1), null, null),
            new(start.AddSeconds(2), double.NaN, null),
            new(start.AddSeconds(3), double.PositiveInfinity, null),
            new(start.AddSeconds(4), -1, null),
            new(start.AddSeconds(5), 20, null)
        ];

        IReadOnlyList<CpuHistorySample> result = CpuHistorySeries.Create(history);

        Assert.Equal(6, result.Count);
        Assert.Equal(10, result[0].Value);
        Assert.All(result.Skip(1).Take(4), sample => Assert.Null(sample.Value));
        Assert.Equal(20, result[5].Value);
    }

    [Fact]
    public void CreateKeepsOnlyTheNewestMinuteCapacity()
    {
        DateTimeOffset start = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        ApplicationHistoryPoint[] history = Enumerable.Range(0, 75)
            .Select(index => new ApplicationHistoryPoint(start.AddSeconds(index), index, null))
            .ToArray();

        IReadOnlyList<CpuHistorySample> result = CpuHistorySeries.Create(history);

        Assert.Equal(60, result.Count);
        Assert.Equal(start.AddSeconds(15), result[0].Timestamp);
        Assert.Equal(start.AddSeconds(74), result[^1].Timestamp);
    }

    [Fact]
    public void NearestIndexSelectsByTimestampNotByIndexProportion()
    {
        DateTimeOffset start = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        // Three samples over a one-hour range, clustered at the end: an
        // index-proportional mapping would pick the middle sample for a pointer
        // located at the second sample's time position.
        CpuHistorySample[] samples =
        [
            new(start, 1),
            new(start.AddMinutes(59), 2),
            new(start.AddHours(1), 3)
        ];
        double width = 600;
        double leftInset = 54;
        double rightInset = 12;
        double xAtSecondSample = leftInset + (width - rightInset - leftInset) * ((start.AddMinutes(59).UtcTicks - start.UtcTicks) / (double)(start.AddHours(1).UtcTicks - start.UtcTicks));

        int nearest = ChartHoverMapper.NearestIndex(
            xAtSecondSample,
            samples,
            start,
            start.AddHours(1),
            isEmbedded: false,
            availableWidth: width);

        Assert.Equal(1, nearest);
    }

    [Fact]
    public void NearestIndexClampsToPlotEdgesAndHandlesEmptyAndDegenerateRanges()
    {
        DateTimeOffset start = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        CpuHistorySample[] samples = [new(start, 1), new(start.AddMinutes(1), 2)];
        double width = 454;

        Assert.Equal(-1, ChartHoverMapper.NearestIndex(
            100, [], null, null, isEmbedded: false, availableWidth: width));
        Assert.Equal(0, ChartHoverMapper.NearestIndex(
            0, samples, start, start.AddMinutes(10), isEmbedded: false, availableWidth: width));
        Assert.Equal(1, ChartHoverMapper.NearestIndex(
            width, samples, start, start.AddMinutes(10), isEmbedded: false, availableWidth: width));
        Assert.Equal(0, ChartHoverMapper.NearestIndex(
            200, samples, start, start, isEmbedded: false, availableWidth: width));
    }

    [Fact]
    public void LayoutKeepsUnavailableIntervalsAsSeparateSegments()
    {
        DateTimeOffset start = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        CpuHistorySample[] samples =
        [
            new(start, 10),
            new(start.AddSeconds(1), 20),
            new(start.AddSeconds(2), null),
            new(start.AddSeconds(3), 30),
            new(start.AddSeconds(4), 40)
        ];

        MetricSparklineLayout layout = MetricSparklineLayout.Create(samples, 300, 120);

        Assert.Equal(2, layout.Segments.Count);
        Assert.All(layout.Segments, segment => Assert.Equal(2, segment.Count));
        Assert.Equal(4, layout.RealSampleCount);
        Assert.Equal(40, layout.Peak);
    }

    [Fact]
    public void LayoutReportsARealZeroWithoutInventingAOnePercentPeak()
    {
        DateTimeOffset start = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        CpuHistorySample[] samples =
        [
            new(start, 0),
            new(start.AddSeconds(1), 0)
        ];

        MetricSparklineLayout layout = MetricSparklineLayout.Create(samples, 300, 120);

        Assert.Equal(0, layout.Peak);
        Assert.Contains("peak 0.0%", layout.Summary, StringComparison.Ordinal);
        Assert.Single(layout.Segments);
        Assert.All(layout.Segments[0], point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
    }
}
