using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitoringXS.App.Controls;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed record HistoryRangeOption(string Label, TimeSpan Duration)
{
    public override string ToString() => Label;
}

public enum HistoryPageState
{
    Loading,
    Ready,
    Empty,
    ApplicationNotFound,
    DatabaseUnavailable,
    QueryError,
    Cancelled
}

internal enum HistoryValueKind
{
    Percent,
    Bytes,
    BytesPerSecond
}

internal sealed record HistoryMetricDefinition(
    MetricHistoryMetric Metric,
    string Title,
    HistoryValueKind ValueKind,
    string UnitText = "",
    bool UsesPercentScale = false);

public sealed partial class HistoryMetricSeries : ObservableObject
{
    internal HistoryMetricSeries(HistoryMetricDefinition definition)
    {
        Definition = definition;
        Title = definition.Title;
        UnitText = definition.UnitText;
        UsesPercentScale = definition.UsesPercentScale;
    }

    internal HistoryMetricDefinition Definition { get; }

    public string Title { get; }

    public string UnitText { get; }

    public bool UsesPercentScale { get; }

    public MetricSparklineScale Scale =>
        UsesPercentScale ? MetricSparklineScale.Percent : MetricSparklineScale.Dynamic;

    [ObservableProperty]
    public partial IList<CpuHistorySample> Samples { get; set; } = Array.Empty<CpuHistorySample>();

    [ObservableProperty]
    public partial string Summary { get; set; } = "No history loaded.";

    [ObservableProperty]
    public partial string StateText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string AccessibilityText { get; set; } = "No history loaded.";

    [ObservableProperty]
    public partial DateTimeOffset? RangeStartUtc { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? RangeEndUtc { get; set; }
}

internal static class HistorySeriesPresentation
{
    public static (IList<CpuHistorySample> Samples, string Summary, string State, string Accessibility)
        Create(
            HistoryMetricDefinition definition,
            MetricHistoryQueryResult result,
            HistoryRangeOption range,
            int maximumPoints)
    {
        if (!result.IsAvailable)
        {
            string unavailableMessage = result.Error ?? "Database unavailable.";
            return (Array.Empty<CpuHistorySample>(), unavailableMessage, "Database unavailable", unavailableMessage);
        }

        MetricHistoryPoint[] ordered = result.Points
            .Select((point, index) => new { Point = point, Index = index })
            .OrderBy(item => item.Point.TimestampUtc.UtcTicks)
            .ThenBy(item => item.Index)
            .GroupBy(item => item.Point.TimestampUtc.UtcTicks)
            .Select(group => group.Last().Point)
            .ToArray();
        List<CpuHistorySample> samples = [];
        string? lifetime = null;
        DateTimeOffset? previousTimestampUtc = null;
        TimeSpan gapThreshold = GapThreshold(ordered, range, maximumPoints);
        foreach (MetricHistoryPoint point in ordered)
        {
            DateTimeOffset timestampUtc = point.TimestampUtc.ToUniversalTime();
            bool lifetimeChanged = lifetime is not null
                && !string.Equals(lifetime, point.ProcessLifetimeKey, StringComparison.Ordinal);
            bool timeGap = previousTimestampUtc is { } previous
                && timestampUtc - previous > gapThreshold;
            if (lifetimeChanged || timeGap)
            {
                DateTimeOffset gapTimestamp = previousTimestampUtc is { } previousTimestamp
                    && timestampUtc > previousTimestamp
                    ? previousTimestamp + ((timestampUtc - previousTimestamp) / 2)
                    : timestampUtc.AddTicks(-1);
                samples.Add(new(gapTimestamp, null));
            }

            double? value = point.Availability is MetricAvailability.Available or MetricAvailability.Partial
                && point.Value is { } measured
                && double.IsFinite(measured)
                && measured >= 0
                    ? measured
                    : null;
            samples.Add(new(timestampUtc, value));
            lifetime = point.ProcessLifetimeKey;
            previousTimestampUtc = timestampUtc;
        }

        CpuHistorySample[] real = samples.Where(sample => sample.Value.HasValue).ToArray();
        int partial = ordered.Count(point => point.Availability == MetricAvailability.Partial);
        int unavailableCount = ordered.Count(point =>
            point.Availability is not MetricAvailability.Available and not MetricAvailability.Partial
            || !point.Value.HasValue);
        int downsampled = ordered.Count(point => point.IsDownsampled);
        IList<CpuHistorySample> display = HistoryPointDecimator.Decimate(samples, maximumPoints);
        if (real.Length == 0)
        {
            string state = ordered.Length == 0 ? "Empty history" : "Unavailable";
            string summary = ordered.Length == 0
                ? $"No {definition.Title} history in the selected {range.Label} range."
                : $"{definition.Title} has no measured values; unavailable samples remain chart gaps.";
            return (display, summary, state, summary);
        }

        double minimum = real.Min(sample => sample.Value!.Value);
        double maximum = real.Max(sample => sample.Value!.Value);
        double latest = real[^1].Value!.Value;
        string availability = partial > 0 || unavailableCount > 0
            ? $"Partial; {partial} lower-bound and {unavailableCount} unavailable samples"
            : "Available";
        string summaryText = string.Create(
            CultureInfo.InvariantCulture,
            $"{range.Label}; min {Format(minimum, definition.ValueKind)}, max {Format(maximum, definition.ValueKind)}, latest {Format(latest, definition.ValueKind)}; {availability}; {downsampled} downsampled; {display.Count} displayed of {samples.Count} points.");
        return (display, summaryText, partial > 0 || unavailableCount > 0 ? "Partial" : "Available", $"{definition.Title}. {summaryText}");
    }

    private static TimeSpan GapThreshold(
        IReadOnlyList<MetricHistoryPoint> points,
        HistoryRangeOption range,
        int maximumPoints)
    {
        long[] intervals = points
            .Select(point => point.TimestampUtc.ToUniversalTime().UtcTicks)
            .Order()
            .Zip(points.Select(point => point.TimestampUtc.ToUniversalTime().UtcTicks).Order().Skip(1),
                (first, second) => second - first)
            .Where(interval => interval > 0)
            .Order()
            .ToArray();
        TimeSpan median = intervals.Length == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(intervals[intervals.Length / 2]);
        TimeSpan expected = points.Any(point => point.IsDownsampled)
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromTicks(range.Duration.Ticks / Math.Max(1, maximumPoints));
        TimeSpan cadence = median > TimeSpan.Zero && median < expected ? median : expected;
        return TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, cadence.Ticks * 3));
    }

    internal static string Format(double value, HistoryValueKind kind) => kind switch
    {
        HistoryValueKind.Percent => string.Create(CultureInfo.InvariantCulture, $"{value:0.0}%"),
        HistoryValueKind.Bytes => FormatBytes(value),
        _ => $"{FormatBytes(value)}/s"
    };

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes:0.#} {units[unit]}");
    }
}

internal static class HistoryPointDecimator
{
    public static IList<CpuHistorySample> Decimate(
        IReadOnlyList<CpuHistorySample> samples,
        int maximumPoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoints, 2);
        CpuHistorySample[] normalized = samples
            .Select((sample, index) => new { Sample = sample, Index = index })
            .OrderBy(item => item.Sample.Timestamp.UtcTicks)
            .ThenBy(item => item.Index)
            .GroupBy(item => item.Sample.Timestamp.UtcTicks)
            .Select(group => group.Last().Sample)
            .ToArray();
        if (normalized.Length <= maximumPoints)
        {
            return normalized;
        }

        HashSet<int> selected = [0, normalized.Length - 1];
        AddExtrema(normalized, selected);
        for (int index = 0; index < normalized.Length; index++)
        {
            if (!normalized[index].Value.HasValue)
            {
                selected.Add(index);
                if (index > 0)
                {
                    selected.Add(index - 1);
                }

                if (index + 1 < normalized.Length)
                {
                    selected.Add(index + 1);
                }
            }
        }

        if (selected.Count > maximumPoints)
        {
            int[] mandatory = selected.Order().ToArray();
            selected = SelectEvenly(mandatory, maximumPoints).ToHashSet();
            selected.Add(0);
            selected.Add(normalized.Length - 1);
            while (selected.Count > maximumPoints)
            {
                int removable = selected
                    .Where(index => index is not 0 && index != normalized.Length - 1)
                    .OrderBy(index => Math.Min(index, normalized.Length - 1 - index))
                    .First();
                selected.Remove(removable);
            }
        }

        for (int slot = 1; selected.Count < maximumPoints && slot < maximumPoints - 1; slot++)
        {
            selected.Add((int)Math.Round(
                slot * (normalized.Length - 1d) / (maximumPoints - 1d),
                MidpointRounding.AwayFromZero));
        }

        return selected
            .Order()
            .Take(maximumPoints)
            .Select(index => normalized[index])
            .ToArray();
    }

    private static IEnumerable<int> SelectEvenly(int[] indices, int count) =>
        Enumerable.Range(0, count)
            .Select(slot => indices[(int)Math.Round(
                slot * (indices.Length - 1d) / (count - 1d),
                MidpointRounding.AwayFromZero)])
            .Distinct();

    private static void AddExtrema(CpuHistorySample[] samples, HashSet<int> selected)
    {
        int minimum = -1;
        int maximum = -1;
        for (int index = 0; index < samples.Length; index++)
        {
            if (!samples[index].Value.HasValue)
            {
                continue;
            }

            if (minimum < 0 || samples[index].Value < samples[minimum].Value)
            {
                minimum = index;
            }

            if (maximum < 0 || samples[index].Value > samples[maximum].Value)
            {
                maximum = index;
            }
        }

        if (minimum >= 0)
        {
            selected.Add(minimum);
            selected.Add(maximum);
        }
    }
}
