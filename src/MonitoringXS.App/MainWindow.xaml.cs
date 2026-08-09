using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using MonitoringXS.App.Appearance;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Accessibility;
using Windows.Graphics;
using SettingsTheme = MonitoringXS.Core.Models.ApplicationTheme;

namespace MonitoringXS.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private const double WideToolbarMinimumWidth = 1240;
    private static readonly Uri LightTitleBarLogoUri =
        new("ms-appx:///Assets/Branding/MonitoringXS.Logo.24.png");
    private static readonly Uri DarkTitleBarLogoUri =
        new("ms-appx:///Assets/Branding/MonitoringXS.Logo.Dark.24.png");
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, TabViewItem> _applicationTabs = new(StringComparer.Ordinal);
    private readonly LiveRefreshCadence _cadence;
    private readonly SettingsPageViewModel _settingsViewModel;
    private bool _disposed;
    private bool? _toolbarUsesSingleRow;

    public MainWindow(
        MainWindowViewModel viewModel,
        ApplicationSettings settings,
        HistoryPageViewModel historyViewModel,
        DiagnosticsPageViewModel diagnosticsViewModel,
        SettingsPageViewModel settingsViewModel,
        LiveRefreshCadence cadence,
        LocalizationService localization,
        string initialNavigationTag = "dashboard")
    {
        ViewModel = viewModel;
        _cadence = cadence;
        _settingsViewModel = settingsViewModel;
        InitializeComponent();
        ViewModel.ConfigureApplicationSort(
            settings,
            settingsViewModel.SetApplicationSortAsync,
            _shutdown.Token);
        ViewModel.ConfigureProcessActions(ConfirmProcessActionAsync, _shutdown.Token);
        HistoryWorkspace.Initialize(historyViewModel, _shutdown.Token);
        DiagnosticsWorkspace.Initialize(diagnosticsViewModel, _shutdown.Token);
        SettingsWorkspace.Initialize(settingsViewModel, _shutdown.Token);
        Root.FlowDirection = localization.Direction == TextDirection.RightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        settingsViewModel.ThemeRequested += ApplyAppearance;
        ConfigureTitleBar();
        Root.ActualThemeChanged += Root_ActualThemeChanged;
        ApplyAppearance(settings.Theme);
        AppWindow.Resize(new SizeInt32(1180, 760));
        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;
        PrimaryNavigation.SelectedItem = PrimaryNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), initialNavigationTag, StringComparison.Ordinal));
        RestoreOpenTabs();
    }

    public MainWindowViewModel ViewModel { get; }

    public string SelectedNavigationTag =>
        (PrimaryNavigation.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "dashboard";

    private async Task<bool> ConfirmProcessActionAsync(
        ProcessActionConfirmation request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContentDialog dialog = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = request.Title,
            Content = request.Message,
            PrimaryButtonText = request.ConfirmButtonText,
            CloseButtonText = ((App)Microsoft.UI.Xaml.Application.Current).Localization.Get(
                LocalizationKeys.Cancel),
            DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult result = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary;
    }

    internal void EnableResponsiveToolbar()
    {
        AppWindow.Changed += AppWindow_Changed;
        UpdateToolbarLayout(AppWindow.Size.Width);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
        {
            UpdateToolbarLayout(sender.Size.Width);
        }
    }

    private void UpdateToolbarLayout(double physicalWindowWidth)
    {
        double rasterizationScale = Root.XamlRoot?.RasterizationScale ?? 1;
        bool useSingleRow =
            physicalWindowWidth / rasterizationScale >= WideToolbarMinimumWidth;
        if (_toolbarUsesSingleRow == useSingleRow)
        {
            return;
        }

        _toolbarUsesSingleRow = useSingleRow;
        Grid.SetRow(ToolbarAdvancedGroup, useSingleRow ? 0 : 2);
        Grid.SetRow(ApplicationSearchGroup, useSingleRow ? 0 : 1);
        Grid.SetColumn(ApplicationSearchGroup, useSingleRow ? 1 : 0);
        Grid.SetColumnSpan(ApplicationSearchGroup, useSingleRow ? 1 : 3);
        Grid.SetRow(NoComparableDataText, useSingleRow ? 1 : 2);
    }

    private void ConfigureTitleBar()
    {
        ConfigureWindowIcon();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            UpdateCaptionButtonColors();
        }
    }

    private void ConfigureWindowIcon()
    {
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Branding",
            "MonitoringXS.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void ApplyAppearance(SettingsTheme appearance)
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
        UpdateTitleBarLogo();
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionButtonColors();
        UpdateTitleBarLogo();
    }

    private void UpdateTitleBarLogo()
    {
        bool highContrast = WindowsHighContrastReader.IsEnabled();
        BitmapIconSource iconSource = new()
        {
            ShowAsMonochrome = highContrast,
            UriSource = !highContrast && Root.ActualTheme == ElementTheme.Dark
                ? DarkTitleBarLogoUri
                : LightTitleBarLogoUri
        };
        if (highContrast &&
            Microsoft.UI.Xaml.Application.Current.Resources["PrimaryTextBrush"] is Brush foreground)
        {
            iconSource.Foreground = foreground;
        }

        AppTitleBar.IconSource = iconSource;
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
            ? Microsoft.UI.ColorHelper.FromArgb(255, 244, 244, 245)
            : Microsoft.UI.ColorHelper.FromArgb(255, 17, 27, 36);
        Windows.UI.Color inactiveForeground = dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 179, 179, 186)
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
        => await LiveRefreshLoop.RunAsync(
            ViewModel.RefreshAsync,
            _cadence,
            ViewModel.ReportRefreshFailure,
            cancellationToken);

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
        AddTab(tabViewModel);
    }

    private void AddTab(ApplicationTabViewModel tabViewModel)
    {
        if (_applicationTabs.ContainsKey(tabViewModel.LogicalApplicationId))
        {
            return;
        }

        TabViewItem tab = new()
        {
            IsClosable = true,
            Tag = tabViewModel.LogicalApplicationId,
            Header = tabViewModel,
            HeaderTemplate = (DataTemplate)Root.Resources["ApplicationTabHeaderTemplate"],
            Content = tabViewModel,
            ContentTemplate = (DataTemplate)Root.Resources["ApplicationDetailTemplate"]
        };
        AutomationProperties.SetName(tab, $"{tabViewModel.Title} application tab");
        _applicationTabs.Add(tabViewModel.LogicalApplicationId, tab);
        WorkspaceTabs.TabItems.Add(tab);
        WorkspaceTabs.SelectedItem = tab;
    }

    private void RestoreOpenTabs()
    {
        foreach (ApplicationTabViewModel tab in ViewModel.OpenTabs)
        {
            AddTab(tab);
        }
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

    private async void PrimaryNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        string? selected = args.SelectedItemContainer?.Tag?.ToString();
        bool systemOverviewSelected = selected == "system-overview";
        bool historySelected = selected == "history";
        bool diagnosticsSelected = selected == "diagnostics";
        bool settingsSelected = selected == "settings";
        bool aboutSelected = selected == "about";
        WorkspaceTabs.Visibility =
            systemOverviewSelected || historySelected || diagnosticsSelected || settingsSelected || aboutSelected
                ? Visibility.Collapsed
                : Visibility.Visible;
        SystemOverviewWorkspace.Visibility = systemOverviewSelected ? Visibility.Visible : Visibility.Collapsed;
        HistoryWorkspace.Visibility = historySelected ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsWorkspace.Visibility = diagnosticsSelected ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspace.Visibility = settingsSelected ? Visibility.Visible : Visibility.Collapsed;
        AboutWorkspace.Visibility = aboutSelected ? Visibility.Visible : Visibility.Collapsed;
        if (historySelected)
        {
            await HistoryWorkspace.ActivateAsync();
        }
        else if (diagnosticsSelected)
        {
            await DiagnosticsWorkspace.ActivateAsync();
        }
        else if (settingsSelected)
        {
            await SettingsWorkspace.ActivateAsync();
        }
    }

    private void SortDirection_Click(object sender, RoutedEventArgs args) =>
        ViewModel.ToggleSortDirection();

    private void ApplicationSearchBox_TextChanged(object sender, TextChangedEventArgs args) =>
        ViewModel.SearchText = ApplicationSearchBox.Text;

    private void ClearApplicationSearchButton_Click(object sender, RoutedEventArgs args)
    {
        ViewModel.ClearSearch();
        ApplicationSearchBox.Text = string.Empty;
        ApplicationSearchBox.Focus(FocusState.Programmatic);
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
        AppWindow.Changed -= AppWindow_Changed;
        Root.ActualThemeChanged -= Root_ActualThemeChanged;
        _settingsViewModel.ThemeRequested -= ApplyAppearance;
        AboutWorkspace.Dispose();
        _shutdown.Cancel();
    }
}
