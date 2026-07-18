using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MonitoringXS.App.ViewModels;

namespace MonitoringXS.App;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, TabViewItem> _applicationTabs = new(StringComparer.Ordinal);

    public MainWindow(MainWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;
    }

    public MainWindowViewModel ViewModel { get; }

    private async void Root_Loaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= Root_Loaded;
        try
        {
            await RunMonitoringLoopAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RunMonitoringLoopAsync(CancellationToken cancellationToken)
    {
        await ViewModel.RefreshAsync(cancellationToken);
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await ViewModel.RefreshAsync(cancellationToken);
        }
    }

    private void ApplicationList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not ApplicationCardViewModel card)
        {
            return;
        }

        if (_applicationTabs.TryGetValue(card.LogicalApplicationId, out TabViewItem? existing))
        {
            WorkspaceTabs.SelectedItem = existing;
            return;
        }

        ApplicationTabViewModel tabViewModel = ViewModel.OpenTab(card);
        TabViewItem tab = new()
        {
            Header = tabViewModel.Title,
            IsClosable = true,
            Tag = card.LogicalApplicationId,
            Content = tabViewModel,
            ContentTemplate = (DataTemplate)Root.Resources["ApplicationDetailTemplate"]
        };
        AutomationProperties.SetName(tab, $"{tabViewModel.Title} application tab");
        _applicationTabs.Add(card.LogicalApplicationId, tab);
        WorkspaceTabs.TabItems.Add(tab);
        WorkspaceTabs.SelectedItem = tab;
    }

    private void WorkspaceTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is not string logicalApplicationId)
        {
            return;
        }

        _applicationTabs.Remove(logicalApplicationId);
        ViewModel.CloseTab(logicalApplicationId);
        sender.TabItems.Remove(args.Tab);
    }

    private void AdvancedMode_Toggled(object sender, RoutedEventArgs args)
    {
        if (AdvancedModeNotice is not null && sender is ToggleSwitch toggle)
        {
            AdvancedModeNotice.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
