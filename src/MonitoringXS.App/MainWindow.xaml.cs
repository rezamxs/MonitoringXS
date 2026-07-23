using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Windowing;
using MonitoringXS.App.Appearance;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Platform.Windows.Accessibility;
using Windows.Graphics;

namespace MonitoringXS.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, TabViewItem> _applicationTabs = new(StringComparer.Ordinal);
    private readonly IAppearancePreferenceStore _appearancePreferenceStore;
    private bool _disposed;
    private bool _appearanceSelectionReady;

    public MainWindow(
        MainWindowViewModel viewModel,
        IAppearancePreferenceStore appearancePreferenceStore,
        AppearanceMode appearance)
    {
        ViewModel = viewModel;
        _appearancePreferenceStore = appearancePreferenceStore;
        SelectedAppearanceOption = AppearanceOptions.Single(option => option.Mode == appearance);
        InitializeComponent();
        ConfigureTitleBar();
        Root.ActualThemeChanged += Root_ActualThemeChanged;
        ApplyAppearance(appearance);
        _appearanceSelectionReady = true;
        AppWindow.Resize(new SizeInt32(1180, 760));
        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;
    }

    public MainWindowViewModel ViewModel { get; }

    public IReadOnlyList<AppearanceOption> AppearanceOptions { get; } =
    [
        new(AppearanceMode.System, "System — follows Windows"),
        new(AppearanceMode.Light, "Light"),
        new(AppearanceMode.Dark, "Dark")
    ];

    public AppearanceOption SelectedAppearanceOption { get; }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            UpdateCaptionButtonColors();
        }
    }

    private async void AppearanceSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_appearanceSelectionReady || AppearanceSelector.SelectedItem is not AppearanceOption option)
        {
            return;
        }

        ApplyAppearance(option.Mode);
        await _appearancePreferenceStore.SaveAsync(option.Mode, CancellationToken.None);
    }

    private void ApplyAppearance(AppearanceMode appearance)
    {
        AppearanceThemeChoice theme = AppearanceThemeResolver.Resolve(
            appearance,
            WindowsHighContrastReader.IsEnabled());
        Root.RequestedTheme = theme switch
        {
            AppearanceThemeChoice.Light => ElementTheme.Light,
            AppearanceThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateCaptionButtonColors();
        UpdateResolvedAppearanceState();
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionButtonColors();
        UpdateResolvedAppearanceState();
    }

    private void UpdateResolvedAppearanceState()
    {
        string resolvedState = AppearancePresentation.ResolvedStateLabel(
            Root.ActualTheme == ElementTheme.Dark);
        ResolvedAppearanceText.Text = resolvedState;

        if (AppearanceSelector.SelectedItem is AppearanceOption option)
        {
            AutomationProperties.SetName(
                AppearanceSelector,
                $"Application appearance. {option.Label}. {resolvedState}.");
        }
    }

    private void UpdateCaptionButtonColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        if (WindowsHighContrastReader.IsEnabled())
        {
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            return;
        }

        bool dark = Root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color foreground = dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 242, 247, 250)
            : Microsoft.UI.ColorHelper.FromArgb(255, 17, 27, 36);
        Windows.UI.Color inactiveForeground = dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 169, 186, 198)
            : Microsoft.UI.ColorHelper.FromArgb(255, 82, 101, 117);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = dark
            ? Microsoft.UI.ColorHelper.FromArgb(24, 255, 255, 255)
            : Microsoft.UI.ColorHelper.FromArgb(16, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = dark
            ? Microsoft.UI.ColorHelper.FromArgb(42, 255, 255, 255)
            : Microsoft.UI.ColorHelper.FromArgb(28, 0, 0, 0);
    }

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
        args.ItemContainer.Style = (Style)Root.Resources[
            isApplication
                ? "ApplicationCardListItemStyle"
                : "ApplicationSectionListItemStyle"];
        args.ItemContainer.IsTabStop = isApplication;
        args.ItemContainer.IsHitTestVisible = isApplication;
        if (args.Item is IApplicationListItemViewModel item)
        {
            // Keep screen-reader text live; a one-time assignment would stay on warm-up values after refresh.
            args.ItemContainer.SetBinding(AutomationProperties.NameProperty, new Binding
            {
                Source = item,
                Path = new PropertyPath(nameof(IApplicationListItemViewModel.AutomationName)),
                Mode = BindingMode.OneWay
            });
        }
        else
        {
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
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

    private void SortDirection_Click(object sender, RoutedEventArgs args) =>
        ViewModel.ToggleSortDirection();

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
        Root.ActualThemeChanged -= Root_ActualThemeChanged;
        _shutdown.Cancel();
    }
}
