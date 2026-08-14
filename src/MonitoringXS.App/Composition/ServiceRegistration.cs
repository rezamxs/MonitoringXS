using Microsoft.Extensions.DependencyInjection;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Application;
using MonitoringXS.Collectors;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Attribution;
using MonitoringXS.Platform.Windows.Broker;
using MonitoringXS.Platform.Windows.Catalogs;
using MonitoringXS.Platform.Windows.Icons;
using MonitoringXS.Platform.Windows.Metadata;
using MonitoringXS.Platform.Windows.Metrics;
using MonitoringXS.Platform.Windows.Packages;
using MonitoringXS.Platform.Windows.Processes;
using MonitoringXS.Platform.Windows.Security;
using MonitoringXS.Storage.Attribution;
using MonitoringXS.Storage.History;
using MonitoringXS.Storage.Settings;

namespace MonitoringXS.App.Composition;

internal static class ServiceRegistration
{
    public static IServiceCollection AddMonitoringXs(this IServiceCollection services) => services
        .AddMonitoringStorage()
        .AddWindowsPlatform()
        .AddMonitoringCollectors()
        .AddMonitoringApplication()
        .AddMonitoringUi();

    private static IServiceCollection AddMonitoringStorage(this IServiceCollection services)
    {
        services.AddSingleton<IUserAttributionOverrideStore>(_ =>
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new JsonUserAttributionOverrideStore(
                Path.Combine(localData, "MonitoringXS", "attribution-overrides.json"));
        });
        services.AddSingleton<IApplicationSettingsStore>(_ =>
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new JsonApplicationSettingsStore(
                Path.Combine(localData, "MonitoringXS", "settings.json"));
        });
        services.AddSingleton<SqliteMetricHistoryStore>(_ =>
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new SqliteMetricHistoryStore(new SqliteMetricHistoryOptions(
                Path.Combine(localData, "MonitoringXS", "history.db")));
        });
        services.AddSingleton<IMetricHistoryStore>(provider =>
            provider.GetRequiredService<SqliteMetricHistoryStore>());
        services.AddSingleton<IMetricHistoryRetentionController>(provider =>
            provider.GetRequiredService<SqliteMetricHistoryStore>());
        return services;
    }

    private static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IExecutableMetadataProvider, ExecutableMetadataProvider>();
        services.AddSingleton<IInstalledApplicationCatalog, Win32InstalledApplicationCatalog>();
        services.AddSingleton<IPackageApplicationCatalog, MsixPackageApplicationCatalog>();
        services.AddSingleton<IPackageIdentityResolver, WindowsPackageIdentityResolver>();
        services.AddSingleton<IDigitalSignatureInspector, DigitalSignatureInspector>();
        services.AddSingleton<IApplicationIconProvider, WindowsApplicationIconProvider>();
        services.AddSingleton<IProcessDiscoveryService, WindowsProcessDiscoveryService>();
        services.AddSingleton<IProcessActionService, WindowsProcessActionService>();
        services.AddSingleton<IClipboardService, WindowsClipboardService>();
        services.AddSingleton<IApplicationAttributionService, ApplicationAttributionService>();
        services.AddSingleton<IProcessResourceCounterReader, WindowsProcessResourceCounterReader>();
        services.AddSingleton<PrivilegedEtwBrokerClient>();
        services.AddSingleton<IPhysicalDiskEventSource>(provider =>
            provider.GetRequiredService<PrivilegedEtwBrokerClient>());
        services.AddSingleton<INetworkEventSource>(provider =>
            provider.GetRequiredService<PrivilegedEtwBrokerClient>());
        services.AddSingleton<IGpuCounterSource, WindowsGpuPerformanceCounterSource>();
        services.AddSingleton<ISystemOverviewProvider, WindowsSystemOverviewProvider>();
        return services;
    }

    private static IServiceCollection AddMonitoringCollectors(this IServiceCollection services)
    {
        services.AddSingleton<IProcessMetricCollector, ProcessMetricCollector>();
        services.AddSingleton<IMetricAggregationService, MetricAggregationService>();
        services.AddSingleton<IPhysicalDiskMetricCollector, PhysicalDiskMetricCollector>();
        services.AddSingleton<IPhysicalDiskAggregationService, PhysicalDiskAggregationService>();
        services.AddSingleton<INetworkMetricCollector, NetworkMetricCollector>();
        services.AddSingleton<INetworkMetricAggregationService, NetworkMetricAggregationService>();
        services.AddSingleton<IGpuMetricCollector, GpuMetricCollector>();
        services.AddSingleton<IGpuMetricAggregationService, GpuMetricAggregationService>();
        return services;
    }

    private static IServiceCollection AddMonitoringApplication(this IServiceCollection services)
    {
        services.AddSingleton<IMetricCaptureStage, PhysicalDiskMetricStage>();
        services.AddSingleton<IMetricCaptureStage, NetworkMetricStage>();
        services.AddSingleton<IMetricCaptureStage, GpuMetricStage>();
        services.AddSingleton<MonitoringCapturePipeline>();
        services.AddSingleton<SystemOverviewService>();
        services.AddSingleton<MonitoringCoordinator>();
        services.AddSingleton<MonitoringSnapshotHub>();
        services.AddSingleton<IMonitoringSnapshotSource>(provider =>
            provider.GetRequiredService<MonitoringSnapshotHub>());
        services.AddSingleton(_ => new LiveRefreshCadence(
            ApplicationSettings.Default.LiveSamplingInterval));
        services.AddSingleton<MonitoringRuntime>();
        return services;
    }

    private static IServiceCollection AddMonitoringUi(this IServiceCollection services)
    {
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<MetricExplanationService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<HistoryPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<DiagnosticsPageViewModel>();
        return services;
    }
}
