using System.Globalization;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Controls;

public sealed class CpuHistorySample
{
    public CpuHistorySample(DateTimeOffset timestamp, double? value)
        : this(timestamp, value, MetricAvailability.Unavailable, null)
    {
    }

    public CpuHistorySample(
        DateTimeOffset timestamp,
        double? value,
        MetricAvailability availability,
        string? reason)
    {
        Timestamp = timestamp;
        Value = value;
        Availability = availability;
        Reason = reason;
    }

    public DateTimeOffset Timestamp { get; set; }

    public double? Value { get; set; }

    /// <summary>Availability of the underlying stored point; never implied zero.</summary>
    public MetricAvailability Availability { get; set; }

    /// <summary>Storage-provided reason (for example "lower bound" or an access error).</summary>
    public string? Reason { get; set; }
}

public sealed record MetricSparklinePoint(double X, double Y);

/// <summary>
/// Maps a pointer X position onto displayed chart samples by actual timestamp.
/// Rendering positions points proportionally to time across the selected range
/// (see <see cref="MetricSparklineLayout.Create"/>), so a pointer-position to
/// index-proportional mapping would highlight the wrong point whenever
/// timestamps are irregular or gap markers shift spacing; this mapper
/// reproduces the same time-to-X projection used while drawing.
/// </summary>
// ponytail: single O(n) scan per hover index change over the displayed
// (decimated) sample set, ~<=360 points; no allocation, no storage access.
public static class ChartHoverMapper
{
    private const double LeftInset = 54;
    private const double RightInset = 12;
    private const double EmbeddedInset = 2;

    public static int NearestIndex(
        double x,
        IList<CpuHistorySample> samples,
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        bool isEmbedded,
        double availableWidth)
    {
        if (samples.Count == 0)
        {
            return -1;
        }

        double leftInset = isEmbedded ? EmbeddedInset : LeftInset;
        double rightInset = isEmbedded ? EmbeddedInset : RightInset;
        double plotRight = Math.Max(leftInset + 1, availableWidth - rightInset);
        double width = Math.Max(1, plotRight - leftInset);
        double fraction = Math.Clamp((x - leftInset) / width, 0, 1);
        DateTimeOffset startUtc = (rangeStartUtc ?? samples[0].Timestamp).ToUniversalTime();
        DateTimeOffset endUtc = (rangeEndUtc ?? samples[samples.Count - 1].Timestamp).ToUniversalTime();
        long startTicks = startUtc.UtcTicks;
        long endTicks = endUtc.UtcTicks;

        if (endTicks <= startTicks)
        {
            // Degenerate range: fall back to index-proportional selection so the
            // tooltip still resolves to a real sample instead of failing silently.
            int fallback = (int)Math.Round(fraction * (samples.Count - 1));
            return Math.Clamp(fallback, 0, samples.Count - 1);
        }

        long targetTicks = startTicks + (long)(fraction * (endTicks - startTicks));
        int nearestIndex = 0;
        long nearestDistance = long.MaxValue;
        for (int index = 0; index < samples.Count; index++)
        {
            long distance = Math.Abs(samples[index].Timestamp.ToUniversalTime().UtcTicks - targetTicks);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = index;
            }
        }

        return nearestIndex;
    }
}

public enum MetricSparklineScale
{
    Dynamic,
    Percent
}

public sealed record MetricSparklineLayout(
    IReadOnlyList<IReadOnlyList<MetricSparklinePoint>> Segments,
    IReadOnlyList<MetricSparklinePoint> Markers,
    int RealSampleCount,
    double Peak,
    double DomainMinimum,
    double DomainMaximum,
    double PlotLeft,
    double PlotTop,
    double PlotRight,
    double PlotBottom,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    string Summary)
{
    private const double LeftInset = 54;
    private const double RightInset = 12;
    private const double TopInset = 12;
    private const double BottomInset = 28;

    public static MetricSparklineLayout Create(
        IReadOnlyList<CpuHistorySample> samples,
        double availableWidth,
        double availableHeight,
        MetricSparklineScale scale = MetricSparklineScale.Percent,
        DateTimeOffset? rangeStartUtc = null,
        DateTimeOffset? rangeEndUtc = null,
        bool isEmbedded = false)
    {
        CpuHistorySample[] ordered = samples
            .Select((sample, index) => new { Sample = sample, Index = index })
            .Where(item => item.Sample.Timestamp != default)
            .OrderBy(item => item.Sample.Timestamp.UtcTicks)
            .ThenBy(item => item.Index)
            .GroupBy(item => item.Sample.Timestamp.UtcTicks)
            .Select(group => group.Last().Sample)
            .ToArray();
        double[] realValues = ordered
            .Where(sample => sample.Value is { } value
                && double.IsFinite(value)
                && value >= 0)
            .Select(sample => sample.Value!.Value)
            .ToArray();
        int realSampleCount = realValues.Length;
        double peak = realSampleCount == 0 ? 0 : realValues.Max();
        string summary = realSampleCount < 2
            ? "History needs two real samples; unavailable samples are gaps."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Last {realSampleCount} real samples · peak {peak:0.0}%.");

        DateTimeOffset fallbackStart = ordered.FirstOrDefault()?.Timestamp.ToUniversalTime()
            ?? DateTimeOffset.UtcNow;
        DateTimeOffset fallbackEnd = ordered.LastOrDefault()?.Timestamp.ToUniversalTime()
            ?? fallbackStart.AddTicks(1);
        DateTimeOffset startUtc = (rangeStartUtc ?? fallbackStart).ToUniversalTime();
        DateTimeOffset endUtc = (rangeEndUtc ?? fallbackEnd).ToUniversalTime();
        if (endUtc <= startUtc)
        {
            endUtc = startUtc.AddTicks(1);
        }

        (double domainMinimum, double domainMaximum) = Domain(realValues, scale);
        double leftInset = isEmbedded ? 2 : LeftInset;
        double rightInset = isEmbedded ? 2 : RightInset;
        double topInset = isEmbedded ? 4 : TopInset;
        double bottomInset = isEmbedded ? 4 : BottomInset;
        double plotRight = Math.Max(leftInset + 1, availableWidth - rightInset);
        double plotBottom = Math.Max(topInset + 1, availableHeight - bottomInset);
        if (realSampleCount == 0
            || availableWidth <= leftInset + rightInset
            || availableHeight <= topInset + bottomInset)
        {
            return new(
                [],
                [],
                realSampleCount,
                peak,
                domainMinimum,
                domainMaximum,
                leftInset,
                topInset,
                plotRight,
                plotBottom,
                startUtc,
                endUtc,
                summary);
        }

        double width = plotRight - leftInset;
        double height = plotBottom - topInset;
        double domainSize = Math.Max(double.Epsilon, domainMaximum - domainMinimum);
        long firstTicks = startUtc.UtcTicks;
        long durationTicks = Math.Max(1, endUtc.UtcTicks - firstTicks);
        List<IReadOnlyList<MetricSparklinePoint>> segments = [];
        List<MetricSparklinePoint> markers = [];
        List<MetricSparklinePoint> currentSegment = [];

        foreach (CpuHistorySample sample in ordered)
        {
            if (sample.Value is not { } value
                || !double.IsFinite(value)
                || value < 0)
            {
                AddVisibleSegment(segments, markers, currentSegment);
                currentSegment = [];
                continue;
            }

            double x = leftInset + width * (sample.Timestamp.ToUniversalTime().UtcTicks - firstTicks) / durationTicks;
            double y = topInset + height * (1 - (value - domainMinimum) / domainSize);
            MetricSparklinePoint projected = new(
                Math.Clamp(x, leftInset, plotRight),
                Math.Clamp(y, topInset, plotBottom));
            if (currentSegment.Count > 0
                && Math.Abs(currentSegment[^1].X - projected.X) < 0.1)
            {
                currentSegment[^1] = projected;
            }
            else
            {
                currentSegment.Add(projected);
            }
        }

        AddVisibleSegment(segments, markers, currentSegment);
        return new(
            segments,
            markers,
            realSampleCount,
            peak,
            domainMinimum,
            domainMaximum,
            leftInset,
            topInset,
            plotRight,
            plotBottom,
            startUtc,
            endUtc,
            summary);
    }

    private static (double Minimum, double Maximum) Domain(
        double[] values,
        MetricSparklineScale scale)
    {
        if (scale == MetricSparklineScale.Percent)
        {
            return (0, 100);
        }

        if (values.Length == 0)
        {
            return (0, 1);
        }

        double[] ordered = values.Order().ToArray();
        double minimum;
        double maximum;
        if (ordered.Length >= 5)
        {
            minimum = Percentile(ordered, 0.05);
            maximum = Percentile(ordered, 0.95);
        }
        else
        {
            minimum = ordered[0];
            maximum = ordered[^1];
        }

        if (maximum <= minimum)
        {
            double padding = Math.Max(1, Math.Abs(minimum) * 0.1);
            return (minimum - padding, maximum + padding);
        }

        double range = maximum - minimum;
        return (Math.Max(0, minimum - range * 0.1), maximum + range * 0.1);
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        double position = percentile * (ordered.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        double fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }

    private static void AddVisibleSegment(
        List<IReadOnlyList<MetricSparklinePoint>> segments,
        List<MetricSparklinePoint> markers,
        List<MetricSparklinePoint> segment)
    {
        if (segment.Count >= 2)
        {
            segments.Add(segment);
        }
        else if (segment.Count == 1)
        {
            markers.Add(segment[0]);
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

    public static IReadOnlyList<CpuHistorySample> CreateMemory(
        IReadOnlyList<ApplicationHistoryPoint> history) => history
        .Select((point, index) => new IndexedHistoryPoint(point, index))
        .GroupBy(item => item.Point.Timestamp)
        .Select(group => group.MaxBy(item => item.Index)!)
        .OrderBy(item => item.Point.Timestamp)
        .TakeLast(Capacity)
        .Select(item => new CpuHistorySample(
            item.Point.Timestamp,
            SanitizeBytes(item.Point.WorkingSetBytes)))
        .ToArray();

    private static double? Sanitize(double? value) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value >= 0
            ? value.Value
            : null;

    private static double? SanitizeBytes(long? value) =>
        value.HasValue && value.Value >= 0
            ? value.Value
            : null;

    private sealed record IndexedHistoryPoint(ApplicationHistoryPoint Point, int Index);
}
