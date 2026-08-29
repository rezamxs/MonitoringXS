using CommunityToolkit.Mvvm.ComponentModel;

namespace MonitoringXS.App.ViewModels;

/// <summary>
/// Mutable, observable wrapper around metric explanation data.
/// Allows in-place property updates without replacing the parent collection,
/// which prevents ScrollViewer jumps and preserves Expander state during live refresh.
/// </summary>
public sealed partial class MetricExplanationViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BeginnerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AdvancedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// True when the metric is in a non-healthy state that should be explicitly shown.
    /// Healthy (Available) metrics hide the redundant status text.
    /// </summary>
    [ObservableProperty]
    public partial bool HasVisibleStatus { get; set; }

    /// <summary>
    /// Expansion state for the advanced details expander.
    /// Preserved across live refresh updates because items are updated in place.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public MetricExplanationViewModel()
    {
    }

    public MetricExplanationViewModel(MetricExplanationItem source)
    {
        Name = source.Name;
        BeginnerText = source.BeginnerText;
        StatusText = source.StatusText;
        AdvancedText = source.AdvancedText;
        ProviderName = source.ProviderName;
        HasVisibleStatus = !source.IsHealthy;
    }

    /// <summary>
    /// Updates all properties from a source item without replacing this instance.
    /// </summary>
    public void Update(MetricExplanationItem source)
    {
        Name = source.Name;
        BeginnerText = source.BeginnerText;
        StatusText = source.StatusText;
        AdvancedText = source.AdvancedText;
        ProviderName = source.ProviderName;
        HasVisibleStatus = !source.IsHealthy;
    }
}