namespace MonitoringXS.App.ViewModels;

using MonitoringXS.App.Localization;

internal static class ApplicationSortPresentation
{
    public static bool DefaultDescending(ApplicationSortField field) =>
        field != ApplicationSortField.ApplicationName;

    public static string DirectionLabel(
        ApplicationSortField field,
        bool descending,
        LocalizationService? localization = null) =>
        field == ApplicationSortField.ApplicationName
            ? descending
                ? localization?.Get(LocalizationKeys.SortZToA) ?? "Z to A"
                : localization?.Get(LocalizationKeys.SortAToZ) ?? "A to Z"
            : descending
                ? localization?.Get(LocalizationKeys.SortHighestToLowest) ?? "Highest to lowest"
                : localization?.Get(LocalizationKeys.SortLowestToHighest) ?? "Lowest to highest";

    public static string DirectionAutomationName(
        ApplicationSortField field,
        bool descending,
        LocalizationService? localization = null)
    {
        string current = DirectionLabel(field, descending, localization);
        string next = DirectionLabel(field, !descending, localization);
        return localization?.Format(LocalizationKeys.SortDirectionAutomation, current, next)
            ?? $"Sort direction: {current}. Activate to change to {next}.";
    }
}
