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
    string UnitText = "");

public sealed partial class HistoryMetricSeries : ObservableObject
{
    internal HistoryMetricSeries(HistoryMetricDefinition definition)
    {
        Definition = definition;
        Title = definition.Title;
        UnitText = definition.UnitText;
    }

    internal HistoryMetricDefinition Definition { get; }

    public string Title { get; }

    public string UnitText { get; }

    [ObservableProperty]
    public partial IList<CpuHistorySample> Samples { get; set; } = Array.Empty<CpuHistorySample>();

    [ObservableProperty]
    public partial string Summary { get; set; } = "No history loaded.";

    [ObservableProperty]
    public partial string StateText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string AccessibilityText { get; set; } = "No history loaded.";
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
            .OrderBy(point => point.TimestampUtc)
            .ThenBy(point => point.ProcessLifetimeKey, StringComparer.Ordinal)
            .ToArray();
        List<CpuHistorySample> samples = [];
        string? lifetime = null;
        foreach (MetricHistoryPoint point in ordered)
        {
            if (lifetime is not null
                && !string.Equals(lifetime, point.ProcessLifetimeKey, StringComparison.Ordinal))
            {
                samples.Add(new(point.TimestampUtc.ToLocalTime().AddTicks(-1), null));
            }

            double? value = point.Availability is MetricAvailability.Available or MetricAvailability.Partial
                && point.Value is { } measured
                && double.IsFinite(measured)
                && measured >= 0
                    ? measured
                    : null;
            samples.Add(new(point.TimestampUtc.ToLocalTime(), value));
            lifetime = point.ProcessLifetimeKey;
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
        if (samples.Count <= maximumPoints)
        {
            return samples.ToArray();
        }

        HashSet<int> selected = [0, samples.Count - 1];
        AddExtrema(samples, selected);
        for (int index = 0; index < samples.Count; index++)
        {
            if (!samples[index].Value.HasValue)
            {
                selected.Add(index);
                if (index > 0)
                {
                    selected.Add(index - 1);
                }

                if (index + 1 < samples.Count)
                {
                    selected.Add(index + 1);
                }
            }
        }

        for (int slot = 1; selected.Count < maximumPoints && slot < maximumPoints - 1; slot++)
        {
            selected.Add((int)Math.Round(
                slot * (samples.Count - 1d) / (maximumPoints - 1d),
                MidpointRounding.AwayFromZero));
        }

        int[] ordered = selected.Order().ToArray();
        if (ordered.Length > maximumPoints)
        {
            ordered = Enumerable.Range(0, maximumPoints)
                .Select(slot => ordered[(int)Math.Round(
                    slot * (ordered.Length - 1d) / (maximumPoints - 1d),
                    MidpointRounding.AwayFromZero)])
                .Distinct()
                .ToArray();
        }

        return ordered.Select(index => samples[index]).ToArray();
    }

    private static void AddExtrema(IReadOnlyList<CpuHistorySample> samples, HashSet<int> selected)
    {
        int minimum = -1;
        int maximum = -1;
        for (int index = 0; index < samples.Count; index++)
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
