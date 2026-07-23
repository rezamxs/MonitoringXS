namespace MonitoringXS.App.ViewModels;

internal static class ApplicationSortPresentation
{
    public static bool DefaultDescending(ApplicationSortField field) =>
        field != ApplicationSortField.ApplicationName;

    public static string DirectionLabel(ApplicationSortField field, bool descending) =>
        field == ApplicationSortField.ApplicationName
            ? descending ? "Z to A" : "A to Z"
            : descending ? "Highest to lowest" : "Lowest to highest";

    public static string DirectionAutomationName(ApplicationSortField field, bool descending)
    {
        string current = DirectionLabel(field, descending);
        string next = DirectionLabel(field, !descending);
        return $"Sort direction: {current}. Activate to change to {next}.";
    }
}
