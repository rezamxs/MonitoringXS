using System.Globalization;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public static class ApplicationCardSorter
{
    public static IReadOnlyList<ApplicationCardViewModel> Sort(
        IEnumerable<ApplicationCardViewModel> cards,
        ApplicationSortField field,
        bool descending,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        List<ApplicationCardViewModel> result = [.. cards];
        result.Sort(new CardComparer(field, descending, culture ?? CultureInfo.CurrentCulture));
        return result;
    }

    public static bool HasComparableData(
        IEnumerable<ApplicationCardViewModel> cards,
        ApplicationSortField field)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards.Any(card =>
            field is ApplicationSortField.ApplicationName or ApplicationSortField.ProcessId
            || GetMetricValue(card, field).HasValue);
    }

    private sealed class CardComparer(
        ApplicationSortField field,
        bool descending,
        CultureInfo culture)
        : IComparer<ApplicationCardViewModel>
    {
        public int Compare(ApplicationCardViewModel? left, ApplicationCardViewModel? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            if (field == ApplicationSortField.ApplicationName)
            {
                int nameResult = CompareName(left, right, culture);
                return ApplyDirection(nameResult, descending);
            }

            MetricSortValue leftValue = GetMetricValue(left, field);
            MetricSortValue rightValue = GetMetricValue(right, field);
            if (leftValue.HasValue != rightValue.HasValue)
            {
                // Measured values always precede warming-up, denied, unsupported, and other unavailable states.
                return leftValue.HasValue ? -1 : 1;
            }

            if (leftValue.HasValue)
            {
                int metricResult = leftValue.Value.CompareTo(rightValue.Value);
                if (metricResult != 0)
                {
                    return ApplyDirection(metricResult, descending);
                }
            }

            // When both items lack metric values and both are truly unavailable
            // (not merely warming up or partially measured), skip availability rank
            // and use deterministic name order for predictability.
            bool bothTrulyUnavailable = !leftValue.HasValue && !rightValue.HasValue
                && !IsPotentiallyAvailable(leftValue.Availability)
                && !IsPotentiallyAvailable(rightValue.Availability);

            if (!bothTrulyUnavailable)
            {
                int availabilityResult = AvailabilityRank(leftValue.Availability)
                    .CompareTo(AvailabilityRank(rightValue.Availability));
                if (availabilityResult != 0)
                {
                    return availabilityResult;
                }
            }

            int secondaryNameResult = CompareName(left, right, culture);
            if (secondaryNameResult != 0)
            {
                return secondaryNameResult;
            }

            return StringComparer.Ordinal.Compare(left.LogicalApplicationId, right.LogicalApplicationId);
        }
    }

    private static MetricSortValue GetMetricValue(
        ApplicationCardViewModel card,
        ApplicationSortField field)
    {
        ApplicationMetricSnapshot? snapshot = card.LatestSnapshot;
        if (snapshot is null)
        {
            return default;
        }

        return field switch
        {
            ApplicationSortField.CpuUsage => FromMetric(snapshot.CpuPercent),
            ApplicationSortField.ProcessId => snapshot.Processes.Count == 0
                ? default
                : new MetricSortValue(
                    true,
                    snapshot.Processes.Min(process => process.InstanceId.ProcessId),
                    MetricAvailability.Available),
            ApplicationSortField.MemoryUsage => FromMetric(snapshot.WorkingSetBytes),
            ApplicationSortField.ProcessIoRate => CombineRates(
                snapshot.IoReadBytesPerSecond,
                snapshot.IoWriteBytesPerSecond),
            ApplicationSortField.PhysicalDiskRate => CombineRates(
                snapshot.PhysicalDisk.ReadBytesPerSecond,
                snapshot.PhysicalDisk.WriteBytesPerSecond),
            ApplicationSortField.NetworkRate => CombineRates(
                snapshot.Network.DownloadBytesPerSecond,
                snapshot.Network.UploadBytesPerSecond),
            ApplicationSortField.GpuUsage => FromMetric(snapshot.Gpu.UtilizationPercent),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported application sort field.")
        };
    }

    private static MetricSortValue CombineRates(
        MetricValue<double> first,
        MetricValue<double> second)
    {
        MetricSortValue firstValue = FromMetric(first);
        MetricSortValue secondValue = FromMetric(second);
        return firstValue.HasValue && secondValue.HasValue
            ? new MetricSortValue(
                true,
                firstValue.Value + secondValue.Value,
                firstValue.Availability == MetricAvailability.Partial
                    || secondValue.Availability == MetricAvailability.Partial
                    ? MetricAvailability.Partial
                    : MetricAvailability.Available)
            : new(false, 0, WorstAvailability(first.Availability, second.Availability));
    }

    private static MetricSortValue FromMetric(MetricValue<double> metric)
    {
        if (!metric.IsAvailable || !metric.Value.HasValue || !double.IsFinite(metric.Value.Value))
        {
            return new(false, 0, metric.Availability);
        }

        // Partial metrics keep their measured lower-bound value; their availability is never replaced with zero.
        return new MetricSortValue(true, metric.Value.Value, metric.Availability);
    }

    private static MetricSortValue FromMetric(MetricValue<long> metric) =>
        metric.IsAvailable && metric.Value.HasValue
            ? new MetricSortValue(true, metric.Value.Value, metric.Availability)
            : new(false, 0, metric.Availability);

    private static int CompareName(
        ApplicationCardViewModel left,
        ApplicationCardViewModel right,
        CultureInfo culture)
    {
        int insensitive = culture.CompareInfo.Compare(
            left.DisplayName,
            right.DisplayName,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        return insensitive != 0
            ? insensitive
            : StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName);
    }

    private static MetricAvailability WorstAvailability(
        MetricAvailability first,
        MetricAvailability second) => AvailabilityRank(first) >= AvailabilityRank(second)
        ? first
        : second;

    private static bool IsPotentiallyAvailable(MetricAvailability availability) =>
        availability is MetricAvailability.Available or MetricAvailability.Partial or MetricAvailability.WarmingUp;

    private static int AvailabilityRank(MetricAvailability availability) => availability switch
    {
        MetricAvailability.Available => 0,
        MetricAvailability.Partial => 1,
        MetricAvailability.WarmingUp => 2,
        MetricAvailability.Unavailable => 3,
        MetricAvailability.AccessDenied => 4,
        MetricAvailability.Unsupported => 5,
        _ => 6
    };

    private static int ApplyDirection(int result, bool descending) => descending
        ? result switch
        {
            < 0 => 1,
            > 0 => -1,
            _ => 0
        }
        : result;

    private readonly record struct MetricSortValue(
        bool HasValue,
        double Value,
        MetricAvailability Availability);
}
