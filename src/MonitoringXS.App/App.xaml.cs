using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Collectors;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Platform.Windows.Attribution;
using MonitoringXS.Platform.Windows.Metrics;
using MonitoringXS.Platform.Windows.Processes;

namespace MonitoringXS.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    public IServiceProvider Services { get; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(Services.GetRequiredService<MainWindowViewModel>());
        _window.Activate();
    }

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        services.AddSingleton<IProcessDiscoveryService, WindowsProcessDiscoveryService>();
        services.AddSingleton<IApplicationAttributionService, ApplicationAttributionService>();
        services.AddSingleton<IProcessIoCounterReader, WindowsProcessIoCounterReader>();
        services.AddSingleton<IProcessMetricCollector, ProcessMetricCollector>();
        services.AddSingleton<IMetricAggregationService, MetricAggregationService>();
        services.AddSingleton<MonitoringCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
