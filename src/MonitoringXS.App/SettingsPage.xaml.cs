using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;
using SettingsTheme = MonitoringXS.Core.Models.ApplicationTheme;

namespace MonitoringXS.App;

public sealed partial class SettingsPage : UserControl
{
    private CancellationToken _lifetimeToken;
    private bool _initialized;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    public void Initialize(SettingsPageViewModel viewModel, CancellationToken lifetimeToken)
    {
        DataContext = viewModel;
        _lifetimeToken = lifetimeToken;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs args)
    {
        Loaded -= SettingsPage_Loaded;
        SamplingSelector.SelectionChanged += SamplingSelector_SelectionChanged;
        RetentionSelector.SelectionChanged += RetentionSelector_SelectionChanged;
        ThemeSelector.SelectionChanged += ThemeSelector_SelectionChanged;
        _initialized = true;
    }

    public async Task ActivateAsync()
    {
        if (DataContext is SettingsPageViewModel viewModel
            && viewModel.RefreshBrokerStatusCommand.CanExecute(null))
        {
            await IgnoreBrokerCancellationAsync(
                viewModel.RefreshBrokerStatusCommand.ExecuteAsync(null),
                viewModel);
        }
    }

    private async void SamplingSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_initialized
            && DataContext is SettingsPageViewModel viewModel
            && SamplingSelector.SelectedItem is SettingsOption<int> option)
        {
            await IgnoreShutdownCancellationAsync(
                viewModel.SetSamplingAsync(option, _lifetimeToken));
        }
    }

    private async void RetentionSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_initialized
            && DataContext is SettingsPageViewModel viewModel
            && RetentionSelector.SelectedItem is SettingsOption<int> option)
        {
            await IgnoreShutdownCancellationAsync(
                viewModel.SetRetentionAsync(option, _lifetimeToken));
        }
    }

    private async void ThemeSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_initialized
            && DataContext is SettingsPageViewModel viewModel
            && ThemeSelector.SelectedItem is SettingsOption<SettingsTheme> option)
        {
            await IgnoreShutdownCancellationAsync(
                viewModel.SetThemeAsync(option, _lifetimeToken));
        }
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs args)
    {
        if (DataContext is SettingsPageViewModel viewModel)
        {
            viewModel.RefreshBrokerStatusCommand.Cancel();
        }
    }

    private static async Task IgnoreBrokerCancellationAsync(
        Task operation,
        SettingsPageViewModel viewModel)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
            when (viewModel.RefreshBrokerStatusCommand.IsCancellationRequested)
        {
        }
    }

    private async Task IgnoreShutdownCancellationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
    }
}
