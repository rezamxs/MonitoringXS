using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using MonitoringXS.App.ViewModels;
using Windows.Graphics;

namespace MonitoringXS.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, TabViewItem> _applicationTabs = new(StringComparer.Ordinal);
    private bool _disposed;

    public MainWindow(MainWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1180, 760));
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
        finally
        {
            _shutdown.Dispose();
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
            IsClosable = true,
            Tag = card.LogicalApplicationId,
            Content = tabViewModel,
            ContentTemplate = (DataTemplate)Root.Resources["ApplicationDetailTemplate"]
        };
        tab.SetBinding(TabViewItem.HeaderProperty, new Binding
        {
            Source = tabViewModel,
            Path = new PropertyPath(nameof(ApplicationTabViewModel.Title)),
            Mode = BindingMode.OneWay
        });
        AutomationProperties.SetName(tab, $"{tabViewModel.Title} application tab");
        _applicationTabs.Add(card.LogicalApplicationId, tab);
        WorkspaceTabs.TabItems.Add(tab);
        WorkspaceTabs.SelectedItem = tab;
    }

    private void ApplicationList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        bool isApplication = args.Item is ApplicationCardViewModel;
        args.ItemContainer.IsTabStop = isApplication;
        args.ItemContainer.IsHitTestVisible = isApplication;
        if (args.Item is IApplicationListItemViewModel item)
        {
            AutomationProperties.SetName(args.ItemContainer, item.AutomationName);
        }
        else
        {
            AutomationProperties.SetName(args.ItemContainer, string.Empty);
        }
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
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
    }
}
