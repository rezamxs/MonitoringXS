using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Attribution;

namespace MonitoringXS.IntegrationTests;

public sealed class ApplicationAttributionServiceTests
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddMinutes(-2);

    [Theory]
    [InlineData("chrome", "google-chrome")]
    [InlineData("msedge", "microsoft-edge")]
    public async Task BrowserProcessesAreGroupedByLogicalIdentity(string executable, string expectedId)
    {
        ProcessDescriptor[] processes = [Process(10, executable, true), Process(11, executable, false)];

        AttributionResult[] results = (await CreateService().AttributeAsync(processes, TestContext.Current.CancellationToken)).ToArray();

        Assert.All(results, item => Assert.Equal(expectedId, item.Application?.LogicalApplicationId));
    }

    [Fact]
    public async Task SteamGameRemainsSeparateFromLauncher()
    {
        ProcessDescriptor steam = Process(20, "steam", true, @"C:\Program Files (x86)\Steam\steam.exe");
        ProcessDescriptor game = Process(21, "ExampleGame", true, @"C:\Program Files (x86)\Steam\steamapps\common\Example Game\ExampleGame.exe", parent: 20);

        AttributionResult[] results = (await CreateService().AttributeAsync([steam, game], TestContext.Current.CancellationToken)).ToArray();

        Assert.Equal("steam", results[0].Application?.LogicalApplicationId);
        Assert.NotEqual(results[0].Application?.LogicalApplicationId, results[1].Application?.LogicalApplicationId);
    }

    [Fact]
    public async Task EpicGameRemainsSeparateFromLauncher()
    {
        ProcessDescriptor launcher = Process(30, "EpicGamesLauncher", true, @"C:\Program Files\Epic Games\Launcher\EpicGamesLauncher.exe");
        ProcessDescriptor game = Process(31, "ExampleGame", true, @"C:\Program Files\Epic Games\ExampleGame\Binaries\ExampleGame.exe", parent: 30);

        AttributionResult[] results = (await CreateService().AttributeAsync([launcher, game], TestContext.Current.CancellationToken)).ToArray();

        Assert.Equal("epic-games-launcher", results[0].Application?.LogicalApplicationId);
        Assert.NotEqual(results[0].Application?.LogicalApplicationId, results[1].Application?.LogicalApplicationId);
    }

    [Fact]
    public async Task DirectVsCodeNodeHelperIsGroupedButUnrelatedNodeIsNot()
    {
        ProcessDescriptor code = Process(40, "Code", true, @"C:\Users\dev\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        ProcessDescriptor helper = Process(41, "node", false, @"C:\Users\dev\AppData\Local\Programs\Microsoft VS Code\node.exe", parent: 40, product: "Node.js");
        ProcessDescriptor unrelated = Process(42, "node", true, @"C:\dev\node.exe", product: "Node.js");

        AttributionResult[] results = (await CreateService().AttributeAsync([code, helper, unrelated], TestContext.Current.CancellationToken)).ToArray();

        Assert.Equal("visual-studio-code", results[1].Application?.LogicalApplicationId);
        Assert.NotEqual("visual-studio-code", results[2].Application?.LogicalApplicationId);
    }

    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("notepad")]
    [InlineData("CalculatorApp")]
    public async Task UserFacingMicrosoftApplicationsRemainVisible(string executable)
    {
        AttributionResult result = Assert.Single(await CreateService().AttributeAsync(
            [Process(50, executable, true, @"C:\Windows\System32\app.exe")],
            TestContext.Current.CancellationToken));

        Assert.False(result.IsHidden);
        Assert.NotNull(result.Application);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("csrss")]
    [InlineData("svchost")]
    [InlineData("services")]
    public async Task CriticalInfrastructureIsHidden(string executable)
    {
        AttributionResult result = Assert.Single(await CreateService().AttributeAsync(
            [Process(60, executable, false, @"C:\Windows\System32\system.exe")],
            TestContext.Current.CancellationToken));

        Assert.True(result.IsHidden);
    }

    [Fact]
    public async Task SignedPortableToolIsPlacedInPortableDisposition()
    {
        ProcessDescriptor tool = Process(70, "UsefulTool", true, @"C:\Tools\UsefulTool.exe", product: "Useful Tool", publisher: "Example Publisher");
        DigitalSignatureInfo signature = new(
            DigitalSignatureStatus.CertificatePresent,
            "Example Publisher",
            "CN=Example Publisher",
            "AA",
            "test");

        AttributionResult result = Assert.Single(await CreateService(signature: signature).AttributeAsync(
            [tool],
            TestContext.Current.CancellationToken));

        Assert.Equal(ApplicationDisposition.Portable, result.Application?.Disposition);
        Assert.Contains("signer certificate", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExactWin32CatalogMatchIsInstalledWithCertainConfidence()
    {
        string path = @"C:\Program Files\Example\Example.exe";
        InstalledApplicationCatalogEntry entry = new(
            "win32:example",
            "Example Application",
            "Example Publisher",
            @"C:\Program Files\Example",
            path,
            path,
            "test");

        AttributionResult result = Assert.Single(await CreateService(installed: [entry]).AttributeAsync(
            [Process(80, "Example", true, path, product: "Wrong Metadata")],
            TestContext.Current.CancellationToken));

        Assert.Equal("win32:example", result.Application?.LogicalApplicationId);
        Assert.Equal(ApplicationDisposition.Installed, result.Application?.Disposition);
        Assert.Equal(ClassificationConfidence.Certain, result.Application?.Confidence);
        Assert.Contains("exactly matched", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppUserModelIdMapsProcessToExactMsixApplication()
    {
        ProcessDescriptor process = Process(90, "PackagedApp", true, @"C:\Program Files\WindowsApps\Example\App.exe");
        PackageApplicationCatalogEntry package = new(
            "msix:example.app_123!main",
            "Example.App_123",
            "Example.App_1.0.0.0_x64__123",
            "Example.App_123!Main",
            "Example Package App",
            "Example Publisher",
            @"C:\Program Files\WindowsApps\Example",
            null,
            null);
        Dictionary<int, PackageIdentity> identities = new()
        {
            [90] = new("Example.App_123", package.PackageFullName, "Example.App_123!Main")
        };

        AttributionResult result = Assert.Single(await CreateService(packages: [package], identities: identities)
            .AttributeAsync([process], TestContext.Current.CancellationToken));

        Assert.Equal(package.CatalogId, result.Application?.LogicalApplicationId);
        Assert.Equal(ApplicationDisposition.Packaged, result.Application?.Disposition);
        Assert.Equal(ClassificationConfidence.Certain, result.Application?.Confidence);
        Assert.Contains("AppUserModelID", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistentOverrideTakesPrecedenceOverCatalogEvidence()
    {
        string path = @"C:\Tools\Renamed.exe";
        UserAttributionOverride value = new(
            path,
            "user:renamed",
            "My Renamed App",
            "My Publisher",
            ApplicationDisposition.Installed,
            DateTimeOffset.UtcNow);

        AttributionResult result = Assert.Single(await CreateService(overrides: [value]).AttributeAsync(
            [Process(100, "Renamed", false, path, product: "Old Name")],
            TestContext.Current.CancellationToken));

        Assert.Equal("user:renamed", result.Application?.LogicalApplicationId);
        Assert.Equal("My Renamed App", result.Application?.DisplayName);
        Assert.Equal(ClassificationConfidence.Certain, result.Application?.Confidence);
        Assert.Contains("user attribution override", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackgroundMetadataAloneDoesNotCreateAnApplicationTotal()
    {
        ProcessDescriptor background = Process(
            110,
            "DriverTrayAgent",
            false,
            @"C:\Program Files\Driver\Agent.exe",
            product: "Driver Agent");

        AttributionResult result = Assert.Single(await CreateService().AttributeAsync(
            [background],
            TestContext.Current.CancellationToken));

        Assert.True(result.IsHidden);
        Assert.Contains("No visible window", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogHelperJoinsOnlyWhenAVisiblePeerExists()
    {
        InstalledApplicationCatalogEntry entry = new(
            "win32:suite",
            "Example Suite",
            null,
            @"C:\Program Files\Suite",
            @"C:\Program Files\Suite\Suite.exe",
            null,
            "test");
        ProcessDescriptor visible = Process(120, "Suite", true, entry.PrimaryExecutablePath);
        ProcessDescriptor helper = Process(121, "SuiteHelper", false, @"C:\Program Files\Suite\Helper.exe", product: "Suite Helper");

        AttributionResult[] results = (await CreateService(installed: [entry]).AttributeAsync(
            [visible, helper],
            TestContext.Current.CancellationToken)).ToArray();

        Assert.All(results, result => Assert.False(result.IsHidden));
        Assert.All(results, result => Assert.Equal("win32:suite", result.Application?.LogicalApplicationId));
    }

    [Fact]
    public async Task BroadInstallRootDoesNotClaimAnUnrelatedExecutable()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        InstalledApplicationCatalogEntry overlyBroad = new(
            "win32:broad",
            "Broad Registration",
            null,
            programFiles,
            null,
            null,
            "test");
        string unrelatedPath = Path.Combine(programFiles, "Unrelated", "Tool.exe");

        AttributionResult result = Assert.Single(await CreateService(installed: [overlyBroad]).AttributeAsync(
            [Process(130, "Tool", true, unrelatedPath, product: "Unrelated Tool")],
            TestContext.Current.CancellationToken));

        Assert.Equal(ApplicationDisposition.Portable, result.Application?.Disposition);
        Assert.NotEqual(overlyBroad.CatalogId, result.Application?.LogicalApplicationId);
        Assert.Contains("no Win32 or package catalog match", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationAttributionService CreateService(
        IReadOnlyList<InstalledApplicationCatalogEntry>? installed = null,
        IReadOnlyList<PackageApplicationCatalogEntry>? packages = null,
        IReadOnlyDictionary<int, PackageIdentity>? identities = null,
        IReadOnlyList<UserAttributionOverride>? overrides = null,
        DigitalSignatureInfo? signature = null) => new(
            new InstalledCatalog(installed ?? []),
            new PackageCatalog(packages ?? []),
            new PackageIdentities(identities ?? new Dictionary<int, PackageIdentity>()),
            new Signatures(signature ?? new DigitalSignatureInfo(
                DigitalSignatureStatus.CertificateNotPresent,
                null,
                null,
                null,
                "test")),
            new Overrides(overrides ?? []));

    private ProcessDescriptor Process(
        int pid,
        string name,
        bool visible,
        string? path = null,
        int? parent = null,
        string? product = null,
        string? publisher = null) =>
        new(new ProcessInstanceId(pid, _start.AddMilliseconds(pid)), name, path, product, product, publisher, visible ? name : null, parent, false, visible);

    private sealed class InstalledCatalog(IReadOnlyList<InstalledApplicationCatalogEntry> entries) : IInstalledApplicationCatalog
    {
        public ValueTask<IReadOnlyList<InstalledApplicationCatalogEntry>> GetApplicationsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(entries);
    }

    private sealed class PackageCatalog(IReadOnlyList<PackageApplicationCatalogEntry> entries) : IPackageApplicationCatalog
    {
        public ValueTask<IReadOnlyList<PackageApplicationCatalogEntry>> GetApplicationsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(entries);
    }

    private sealed class PackageIdentities(IReadOnlyDictionary<int, PackageIdentity> identities) : IPackageIdentityResolver
    {
        public ValueTask<PackageIdentity?> ResolveAsync(ProcessDescriptor process, CancellationToken cancellationToken) =>
            ValueTask.FromResult(identities.GetValueOrDefault(process.InstanceId.ProcessId));
    }

    private sealed class Signatures(DigitalSignatureInfo signature) : IDigitalSignatureInspector
    {
        public ValueTask<DigitalSignatureInfo> InspectAsync(string executablePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(signature);
    }

    private sealed class Overrides : IUserAttributionOverrideStore
    {
        private readonly Dictionary<string, UserAttributionOverride> _values;

        public Overrides(IReadOnlyList<UserAttributionOverride> values)
        {
            _values = values.ToDictionary(
                value => Path.GetFullPath(value.ExecutablePath),
                StringComparer.OrdinalIgnoreCase);
        }

        public ValueTask<UserAttributionOverrideSnapshot> GetAllAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UserAttributionOverrideSnapshot(_values, true, null));

        public ValueTask<OverrideMutationResult> UpsertAsync(UserAttributionOverride attributionOverride, CancellationToken cancellationToken) =>
            ValueTask.FromResult(OverrideMutationResult.Success);

        public ValueTask<OverrideMutationResult> RemoveAsync(string executablePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(OverrideMutationResult.Success);
    }
}
