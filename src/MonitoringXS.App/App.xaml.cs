using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using MonitoringXS.App.Composition;
using MonitoringXS.App.ViewModels;
using MonitoringXS.App.Localization;
using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using Microsoft.Windows.Globalization;

namespace MonitoringXS.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly ServiceProvider _services;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        _services = ConfigureServices();
        Localization.LanguageChanged += Localization_LanguageChanged;
    }

    public IServiceProvider Services => _services;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        IApplicationSettingsStore settingsStore =
            _services.GetRequiredService<IApplicationSettingsStore>();
        ApplicationSettingsLoadResult load;
        try
        {
            load = await settingsStore.LoadAsync(CancellationToken.None);
        }
        catch
        {
            load = new(
                ApplicationSettings.Default,
                false,
                false,
                "Settings storage is unavailable.");
        }

        LiveRefreshCadence cadence = _services.GetRequiredService<LiveRefreshCadence>();
        Localization.SetLanguage(load.Settings.Language);
        cadence.Update(load.Settings.LiveSamplingInterval);
        SettingsPageViewModel settingsViewModel =
            _services.GetRequiredService<SettingsPageViewModel>();
        settingsViewModel.Initialize(load);
        MonitoringRuntime runtime = _services.GetRequiredService<MonitoringRuntime>();
        _ = runtime.Start();
        MainWindow mainWindow = new(
            _services.GetRequiredService<MainWindowViewModel>(),
            load.Settings,
            _services.GetRequiredService<HistoryPageViewModel>(),
            _services.GetRequiredService<DiagnosticsPageViewModel>(),
            settingsViewModel,
            _services.GetRequiredService<IMonitoringSnapshotSource>(),
            runtime,
            Localization);
        _window = mainWindow;
        _window.Closed += Window_Closed;
        _window.Activate();
        mainWindow.EnableResponsiveToolbar();
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        if (ReferenceEquals(sender, _window))
        {
            _window = null;
            Localization.LanguageChanged -= Localization_LanguageChanged;
            await _services.GetRequiredService<MonitoringRuntime>().StopAsync();
            await _services.DisposeAsync();
        }
    }

    public LocalizationService Localization => _services.GetRequiredService<LocalizationService>();

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs args)
    {
        ApplicationLanguages.PrimaryLanguageOverride = args.Culture.Name;

        if (_window is not MainWindow oldWindow)
        {
            return;
        }

        string selectedNavigation = oldWindow.SelectedNavigationTag;
        MainWindow replacement = new(
            _services.GetRequiredService<MainWindowViewModel>(),
            _services.GetRequiredService<SettingsPageViewModel>().CurrentSettings,
            _services.GetRequiredService<HistoryPageViewModel>(),
            _services.GetRequiredService<DiagnosticsPageViewModel>(),
            _services.GetRequiredService<SettingsPageViewModel>(),
            _services.GetRequiredService<IMonitoringSnapshotSource>(),
            _services.GetRequiredService<MonitoringRuntime>(),
            Localization,
            selectedNavigation);
        _window = replacement;
        replacement.Closed += Window_Closed;
        replacement.Activate();
        replacement.EnableResponsiveToolbar();
        oldWindow.Close();
    }

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        services.AddMonitoringXs();
        return services.BuildServiceProvider(validateScopes: true);
    }

}
