using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Collectors;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Platform.Windows.Attribution;
using MonitoringXS.Platform.Windows.Catalogs;
using MonitoringXS.Platform.Windows.Icons;
using MonitoringXS.Platform.Windows.Metadata;
using MonitoringXS.Platform.Windows.Metrics;
using MonitoringXS.Platform.Windows.Packages;
using MonitoringXS.Platform.Windows.Processes;
using MonitoringXS.Platform.Windows.Security;
using MonitoringXS.Storage.Attribution;

namespace MonitoringXS.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly ServiceProvider _services;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        _services = ConfigureServices();
    }

    public IServiceProvider Services => _services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_services.GetRequiredService<MainWindowViewModel>());
        _window.Closed += Window_Closed;
        _window.Activate();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _services.Dispose();
    }

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        services.AddSingleton<IExecutableMetadataProvider, ExecutableMetadataProvider>();
        services.AddSingleton<IInstalledApplicationCatalog, Win32InstalledApplicationCatalog>();
        services.AddSingleton<IPackageApplicationCatalog, MsixPackageApplicationCatalog>();
        services.AddSingleton<IPackageIdentityResolver, WindowsPackageIdentityResolver>();
        services.AddSingleton<IDigitalSignatureInspector, DigitalSignatureInspector>();
        services.AddSingleton<IApplicationIconProvider, WindowsApplicationIconProvider>();
        services.AddSingleton<IUserAttributionOverrideStore>(_ =>
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(localData, "MonitoringXS", "attribution-overrides.json");
            return new JsonUserAttributionOverrideStore(path);
        });
        services.AddSingleton<IProcessDiscoveryService, WindowsProcessDiscoveryService>();
        services.AddSingleton<IApplicationAttributionService, ApplicationAttributionService>();
        services.AddSingleton<IProcessResourceCounterReader, WindowsProcessResourceCounterReader>();
        services.AddSingleton<IProcessMetricCollector, ProcessMetricCollector>();
        services.AddSingleton<IMetricAggregationService, MetricAggregationService>();
        services.AddSingleton<EtwPhysicalDiskEventSource>();
        services.AddSingleton<IPhysicalDiskEventSource>(provider =>
            provider.GetRequiredService<EtwPhysicalDiskEventSource>());
        services.AddSingleton<INetworkEventSource>(provider =>
            provider.GetRequiredService<EtwPhysicalDiskEventSource>());
        services.AddSingleton<IPhysicalDiskMetricCollector, PhysicalDiskMetricCollector>();
        services.AddSingleton<IPhysicalDiskAggregationService, PhysicalDiskAggregationService>();
        services.AddSingleton<INetworkMetricCollector, NetworkMetricCollector>();
        services.AddSingleton<INetworkMetricAggregationService, NetworkMetricAggregationService>();
        services.AddSingleton<MonitoringCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
