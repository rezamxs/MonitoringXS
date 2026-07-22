using System.Globalization;
using MonitoringXS.Application;

namespace MonitoringXS.App.Controls;

public sealed class CpuHistorySample
{
    public CpuHistorySample(DateTimeOffset timestamp, double? value)
    {
        Timestamp = timestamp;
        Value = value;
    }

    public DateTimeOffset Timestamp { get; set; }

    public double? Value { get; set; }
}

public sealed record MetricSparklinePoint(double X, double Y);

public sealed record MetricSparklineLayout(
    IReadOnlyList<IReadOnlyList<MetricSparklinePoint>> Segments,
    int RealSampleCount,
    double Peak,
    string Summary)
{
    private const double HorizontalInset = 16;
    private const double TopInset = 12;
    private const double BottomInset = 40;

    public static MetricSparklineLayout Create(
        IReadOnlyList<CpuHistorySample> samples,
        double availableWidth,
        double availableHeight)
    {
        CpuHistorySample[] ordered = samples.OrderBy(sample => sample.Timestamp).ToArray();
        double[] realValues = ordered
            .Where(sample => sample.Value.HasValue)
            .Select(sample => sample.Value!.Value)
            .ToArray();
        int realSampleCount = realValues.Length;
        double peak = realSampleCount == 0 ? 0 : realValues.Max();
        string summary = realSampleCount < 2
            ? "CPU history is warming up. Unavailable samples are not drawn as zero."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Last {realSampleCount} real samples · peak {peak:0.0}% of total CPU capacity.");

        if (ordered.Length < 2 || realSampleCount < 2 || availableWidth <= 24 || availableHeight <= 48)
        {
            return new([], realSampleCount, peak, summary);
        }

        double width = Math.Max(1, availableWidth - (HorizontalInset * 2));
        double height = Math.Max(1, availableHeight - TopInset - BottomInset);
        double scalePeak = Math.Max(1, peak);
        long firstTicks = ordered[0].Timestamp.UtcTicks;
        long durationTicks = Math.Max(1, ordered[^1].Timestamp.UtcTicks - firstTicks);
        List<IReadOnlyList<MetricSparklinePoint>> segments = [];
        List<MetricSparklinePoint> currentSegment = [];

        foreach (CpuHistorySample sample in ordered)
        {
            if (!sample.Value.HasValue)
            {
                AddVisibleSegment(segments, currentSegment);
                currentSegment = [];
                continue;
            }

            double x = HorizontalInset + width * (sample.Timestamp.UtcTicks - firstTicks) / durationTicks;
            double y = TopInset + height * (1 - sample.Value.Value / scalePeak);
            currentSegment.Add(new MetricSparklinePoint(x, y));
        }

        AddVisibleSegment(segments, currentSegment);
        return new(segments, realSampleCount, peak, summary);
    }

    private static void AddVisibleSegment(
        List<IReadOnlyList<MetricSparklinePoint>> segments,
        List<MetricSparklinePoint> segment)
    {
        if (segment.Count >= 2)
        {
            segments.Add(segment);
        }
    }
}

public static class CpuHistorySeries
{
    public const int Capacity = 60;

    public static IReadOnlyList<CpuHistorySample> Create(
        IReadOnlyList<ApplicationHistoryPoint> history) => history
        .Select((point, index) => new IndexedHistoryPoint(point, index))
        .GroupBy(item => item.Point.Timestamp)
        .Select(group => group.MaxBy(item => item.Index)!)
        .OrderBy(item => item.Point.Timestamp)
        .TakeLast(Capacity)
        .Select(item => new CpuHistorySample(
            item.Point.Timestamp,
            Sanitize(item.Point.CpuPercent)))
        .ToArray();

    private static double? Sanitize(double? value) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value >= 0
            ? value.Value
            : null;

    private sealed record IndexedHistoryPoint(ApplicationHistoryPoint Point, int Index);
}
