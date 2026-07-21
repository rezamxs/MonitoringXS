namespace MonitoringXS.App.ViewModels;

public interface IApplicationListItemViewModel
{
    string AutomationName { get; }
}

public sealed record ApplicationSectionViewModel(
    string Title,
    string Description) : IApplicationListItemViewModel
{
    public string AutomationName => $"{Title}. {Description}";
}
