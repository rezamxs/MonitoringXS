using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MonitoringXS.App.ViewModels;

namespace MonitoringXS.App;

public sealed partial class DiagnosticsPage : UserControl
{
    private CancellationToken _lifetimeToken;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Unloaded += DiagnosticsPage_Unloaded;
    }

    public void Initialize(
        DiagnosticsPageViewModel viewModel,
        CancellationToken lifetimeToken)
    {
        DataContext = viewModel;
        _lifetimeToken = lifetimeToken;
    }

    public async Task ActivateAsync()
    {
        if (DataContext is DiagnosticsPageViewModel viewModel
            && viewModel.RefreshCommand.CanExecute(null))
        {
            try
            {
                await viewModel.RefreshCommand.ExecuteAsync(null);
            }
            catch (OperationCanceledException) when (
                _lifetimeToken.IsCancellationRequested
                || viewModel.RefreshCommand.IsCancellationRequested)
            {
            }
        }
    }

    private void DiagnosticsPage_Unloaded(object sender, RoutedEventArgs args)
    {
        if (DataContext is DiagnosticsPageViewModel viewModel)
        {
            viewModel.RefreshCommand.Cancel();
            viewModel.CopySafeSummaryCommand.Cancel();
        }
    }
}
