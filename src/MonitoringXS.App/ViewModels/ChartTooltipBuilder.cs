using System.Globalization;
using MonitoringXS.App.Controls;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

/// <summary>
/// Builds localizable per-point tooltip text for history charts:
/// timestamp, metric name, value + unit, availability, and the reason when
/// a sample is not fully available. Unavailable is never rendered as zero.
/// </summary>
internal static class ChartTooltipBuilder
{
    public static string Build(
        IReadOnlyList<CpuHistorySample> samples,
        int index,
        string metricName,
        HistoryValueKind valueKind,
        string availableText,
        string partialText,
        string unavailableText,
        string reasonText,
        string valueLabel,
        CancellationToken cancellationToken = default)
    {
        if (samples is null || index < 0 || index >= samples.Count)
        {
            return string.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();
        CpuHistorySample sample = samples[index];
        string time = sample.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string nameLine = $"{metricName} · {time}";
        string health = sample.Availability switch
        {
            MetricAvailability.Available => availableText,
            MetricAvailability.Partial => partialText,
            _ => unavailableText
        };
        string value = sample.Value is { } measured && double.IsFinite(measured)
            ? HistorySeriesPresentation.Format(measured, valueKind)
            : unavailableText;
        string? reason = sample.Value.HasValue ? null : sample.Reason;
        string third = health;
        if (reason is not null && reason.Length > 0)
        {
            third = $"{third} · {reasonText}: {reason}";
        }

        return string.Join('\n', nameLine, $"{valueLabel}: {value}", third);
    }
}