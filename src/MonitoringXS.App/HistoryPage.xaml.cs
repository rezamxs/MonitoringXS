using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App;

public sealed partial class HistoryPage : UserControl
{
    private CancellationToken _lifetimeToken;
    private bool _initialized;

    public HistoryPage()
    {
        InitializeComponent();
    }

    public void Initialize(HistoryPageViewModel viewModel, CancellationToken lifetimeToken)
    {
        DataContext = viewModel;
        _lifetimeToken = lifetimeToken;
    }

    public async Task ActivateAsync()
    {
        if (DataContext is not HistoryPageViewModel viewModel)
        {
            return;
        }

        if (!_initialized)
        {
            _initialized = true;
            await viewModel.InitializeAsync(_lifetimeToken);
        }
    }

    private async void ApplicationSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_initialized
            || DataContext is not HistoryPageViewModel viewModel
            || ApplicationSelector.SelectedItem is not MetricHistoryApplication application)
        {
            return;
        }

        await viewModel.SelectApplicationAsync(application, _lifetimeToken);
    }

    private async void RangeSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_initialized
            || DataContext is not HistoryPageViewModel viewModel
            || RangeSelector.SelectedItem is not HistoryRangeOption range)
        {
            return;
        }

        await viewModel.SelectRangeAsync(range, _lifetimeToken);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is HistoryPageViewModel viewModel)
        {
            await viewModel.RefreshAsync(_lifetimeToken);
        }
    }
}
