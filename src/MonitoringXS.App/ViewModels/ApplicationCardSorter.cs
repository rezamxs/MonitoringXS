using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public static class ApplicationCardSorter
{
    public static IReadOnlyList<ApplicationCardViewModel> Sort(
        IEnumerable<ApplicationCardViewModel> cards,
        ApplicationSortField field,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(cards);
        List<ApplicationCardViewModel> result = [.. cards];
        result.Sort(new CardComparer(field, descending));
        return result;
    }

    private sealed class CardComparer(ApplicationSortField field, bool descending)
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
                int nameResult = CompareName(left, right);
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

            int secondaryNameResult = CompareName(left, right);
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
            ApplicationSortField.ProcessCount => new MetricSortValue(true, snapshot.ProcessCount),
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
            ? new MetricSortValue(true, firstValue.Value + secondValue.Value)
            : default;
    }

    private static MetricSortValue FromMetric(MetricValue<double> metric)
    {
        if (!metric.IsAvailable || !metric.Value.HasValue || !double.IsFinite(metric.Value.Value))
        {
            return default;
        }

        // Partial metrics keep their measured lower-bound value; their availability is never replaced with zero.
        return new MetricSortValue(true, metric.Value.Value);
    }

    private static MetricSortValue FromMetric(MetricValue<long> metric) =>
        metric.IsAvailable && metric.Value.HasValue
            ? new MetricSortValue(true, metric.Value.Value)
            : default;

    private static int CompareName(ApplicationCardViewModel left, ApplicationCardViewModel right)
    {
        int insensitive = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        return insensitive != 0
            ? insensitive
            : StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName);
    }

    private static int ApplyDirection(int result, bool descending) => descending
        ? result switch
        {
            < 0 => 1,
            > 0 => -1,
            _ => 0
        }
        : result;

    private readonly record struct MetricSortValue(bool HasValue, double Value);
}
