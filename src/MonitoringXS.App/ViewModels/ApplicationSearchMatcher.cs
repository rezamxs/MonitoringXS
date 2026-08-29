using System.Globalization;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

internal static class ApplicationSearchMatcher
{
    public static bool Matches(
        ApplicationCardViewModel card,
        string? query,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(culture);
        string search = query?.Trim() ?? string.Empty;
        if (search.Length == 0)
        {
            return true;
        }

        ApplicationMetricSnapshot? snapshot = card.LatestSnapshot;
        if (Matches(card.DisplayName, search, culture)
            || Matches(snapshot?.Application.Publisher, search, culture)
            || snapshot?.Processes.Any(process =>
                Matches(process.ProcessName, search, culture)
                || Matches(process.InstanceId.ProcessId.ToString(CultureInfo.InvariantCulture), search, culture)
                || Matches(process.ExecutablePath, search, culture)
                || Matches(process.ProductName, search, culture)
                || Matches(process.FileDescription, search, culture)
                || Matches(process.Publisher, search, culture)) == true)
        {
            return true;
        }

        return snapshot?.Application.Disposition == ApplicationDisposition.Packaged
            && Matches(snapshot.Application.LogicalApplicationId, search, culture);
    }

    private static bool Matches(string? candidate, string search, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(candidate)
        && culture.CompareInfo.IndexOf(
            candidate,
            search,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}
