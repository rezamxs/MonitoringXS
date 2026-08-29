namespace MonitoringXS.App.ViewModels;

internal static class ApplicationSortRefreshPolicy
{
    public static bool IsRefreshDue(
        DateTimeOffset lastRefreshAt,
        DateTimeOffset capturedAt,
        TimeSpan interval,
        bool force) =>
        force
        || lastRefreshAt == DateTimeOffset.MinValue
        || capturedAt < lastRefreshAt
        || capturedAt - lastRefreshAt >= interval;
}
