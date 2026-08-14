using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Xunit;

namespace MonitoringXS.ArchitectureTests;

/// <summary>
/// Machine-enforced architecture boundary tests for the Monitoring XS solution.
/// Rules are derived from the actual project reference graph documented in AGENTS.md.
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    // ponytail: Architecture loaded once per test class; ArchUnitNET caches the model internally.
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssembly(typeof(MonitoringXS.Core.Models.ProcessDescriptor).Assembly)
        .LoadAssembly(typeof(MonitoringXS.Application.MonitoringCoordinator).Assembly)
        .LoadAssembly(typeof(MonitoringXS.Collectors.PhysicalDiskMetricCollector).Assembly)
        .LoadAssembly(typeof(MonitoringXS.Platform.Windows.Metrics.EtwPhysicalDiskEventSource).Assembly)
        .LoadAssembly(typeof(MonitoringXS.Storage.History.SqliteMetricHistoryStore).Assembly)
        .LoadAssembly(typeof(MonitoringXS.DesignSystem.PrecisionGlassTokens).Assembly)
        .LoadAssembly(typeof(MonitoringXS.App.ViewModels.ApplicationSortField).Assembly)
        .LoadAssembly(System.Reflection.Assembly.Load("MonitoringXS.PrivilegedBroker"))
        .Build();

    private static readonly IObjectProvider<IType> CoreTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.Core");

    private static readonly IObjectProvider<IType> ApplicationTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.Application");

    private static readonly IObjectProvider<IType> CollectorsTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.Collectors");

    private static readonly IObjectProvider<IType> PlatformWindowsTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.Platform.Windows");

    private static readonly IObjectProvider<IType> StorageTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.Storage");

    private static readonly IObjectProvider<IType> AppTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.App");

    private static readonly IObjectProvider<IType> DesignSystemTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.DesignSystem");

    private static readonly IObjectProvider<IType> PrivilegedBrokerTypes = ArchRuleDefinition.Types()
        .That().ResideInAssembly("MonitoringXS.PrivilegedBroker");

    [Fact]
    public void Core_Must_Not_Depend_On_Application()
    {
        ArchRuleDefinition.Types().That().Are(CoreTypes)
            .Should().NotDependOnAny(ApplicationTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Core_Must_Not_Depend_On_PlatformWindows()
    {
        ArchRuleDefinition.Types().That().Are(CoreTypes)
            .Should().NotDependOnAny(PlatformWindowsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Core_Must_Not_Depend_On_Collectors()
    {
        ArchRuleDefinition.Types().That().Are(CoreTypes)
            .Should().NotDependOnAny(CollectorsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Core_Must_Not_Depend_On_Storage()
    {
        ArchRuleDefinition.Types().That().Are(CoreTypes)
            .Should().NotDependOnAny(StorageTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Core_Must_Not_Depend_On_App()
    {
        ArchRuleDefinition.Types().That().Are(CoreTypes)
            .Should().NotDependOnAny(AppTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Core_Must_Not_Depend_On_DesignSystem()
    {
        ArchRuleDefinition.Types().That().Are(CoreTypes)
            .Should().NotDependOnAny(DesignSystemTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Application_Must_Not_Depend_On_App()
    {
        ArchRuleDefinition.Types().That().Are(ApplicationTypes)
            .Should().NotDependOnAny(AppTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Application_Must_Not_Depend_On_PlatformWindows()
    {
        ArchRuleDefinition.Types().That().Are(ApplicationTypes)
            .Should().NotDependOnAny(PlatformWindowsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Collectors_Must_Not_Depend_On_App()
    {
        ArchRuleDefinition.Types().That().Are(CollectorsTypes)
            .Should().NotDependOnAny(AppTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Collectors_Must_Not_Depend_On_Application()
    {
        ArchRuleDefinition.Types().That().Are(CollectorsTypes)
            .Should().NotDependOnAny(ApplicationTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Collectors_Must_Not_Depend_On_PlatformWindows()
    {
        ArchRuleDefinition.Types().That().Are(CollectorsTypes)
            .Should().NotDependOnAny(PlatformWindowsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Storage_Must_Not_Depend_On_App()
    {
        ArchRuleDefinition.Types().That().Are(StorageTypes)
            .Should().NotDependOnAny(AppTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Storage_Must_Not_Depend_On_Application()
    {
        ArchRuleDefinition.Types().That().Are(StorageTypes)
            .Should().NotDependOnAny(ApplicationTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Storage_Must_Not_Depend_On_Collectors()
    {
        ArchRuleDefinition.Types().That().Are(StorageTypes)
            .Should().NotDependOnAny(CollectorsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void Storage_Must_Not_Depend_On_PlatformWindows()
    {
        ArchRuleDefinition.Types().That().Are(StorageTypes)
            .Should().NotDependOnAny(PlatformWindowsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PlatformWindows_Must_Not_Depend_On_App()
    {
        ArchRuleDefinition.Types().That().Are(PlatformWindowsTypes)
            .Should().NotDependOnAny(AppTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PlatformWindows_Must_Not_Depend_On_Application()
    {
        ArchRuleDefinition.Types().That().Are(PlatformWindowsTypes)
            .Should().NotDependOnAny(ApplicationTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PlatformWindows_Must_Not_Depend_On_Collectors()
    {
        ArchRuleDefinition.Types().That().Are(PlatformWindowsTypes)
            .Should().NotDependOnAny(CollectorsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PlatformWindows_Must_Not_Depend_On_Storage()
    {
        ArchRuleDefinition.Types().That().Are(PlatformWindowsTypes)
            .Should().NotDependOnAny(StorageTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void DesignSystem_Must_Not_Depend_On_App()
    {
        ArchRuleDefinition.Types().That().Are(DesignSystemTypes)
            .Should().NotDependOnAny(AppTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void DesignSystem_Must_Not_Depend_On_Application()
    {
        ArchRuleDefinition.Types().That().Are(DesignSystemTypes)
            .Should().NotDependOnAny(ApplicationTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PrivilegedBroker_Must_Not_Depend_On_App()
    {
        ArchRuleDefinition.Types().That().Are(PrivilegedBrokerTypes)
            .Should().NotDependOnAny(AppTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PrivilegedBroker_Must_Not_Depend_On_Application()
    {
        ArchRuleDefinition.Types().That().Are(PrivilegedBrokerTypes)
            .Should().NotDependOnAny(ApplicationTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PrivilegedBroker_Must_Not_Depend_On_Collectors()
    {
        ArchRuleDefinition.Types().That().Are(PrivilegedBrokerTypes)
            .Should().NotDependOnAny(CollectorsTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }

    [Fact]
    public void PrivilegedBroker_Must_Not_Depend_On_Storage()
    {
        ArchRuleDefinition.Types().That().Are(PrivilegedBrokerTypes)
            .Should().NotDependOnAny(StorageTypes)
            .WithoutRequiringPositiveResults().Check(Architecture);
    }
}
