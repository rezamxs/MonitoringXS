using System.Diagnostics;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Caching;
using MonitoringXS.Platform.Windows.Catalogs;
using MonitoringXS.Platform.Windows.Icons;
using MonitoringXS.Platform.Windows.Metadata;
using MonitoringXS.Platform.Windows.Packages;
using MonitoringXS.Platform.Windows.Processes;
using MonitoringXS.Platform.Windows.Security;

namespace MonitoringXS.IntegrationTests;

public sealed class MilestoneOneInfrastructureTests
{
    [Fact]
    public void BoundedLruCacheEvictsLeastRecentlyUsedEntry()
    {
        BoundedLruCache<string, int> cache = new(2, StringComparer.Ordinal);
        cache.Set("first", 1);
        cache.Set("second", 2);
        Assert.True(cache.TryGetValue("first", out _));

        cache.Set("third", 3);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGetValue("first", out int first));
        Assert.Equal(1, first);
        Assert.False(cache.TryGetValue("second", out _));
        Assert.True(cache.TryGetValue("third", out int third));
        Assert.Equal(3, third);
    }

    [Fact]
    public async Task ExecutableMetadataCacheIsBoundedAndReusesFileIdentity()
    {
        int reads = 0;
        ExecutableMetadataProvider provider = new(
            path =>
            {
                reads++;
                return Metadata(path);
            },
            capacity: 2);

        await provider.GetMetadataAsync(@"C:\Fake\One.exe", TestContext.Current.CancellationToken);
        await provider.GetMetadataAsync(@"C:\Fake\One.exe", TestContext.Current.CancellationToken);
        await provider.GetMetadataAsync(@"C:\Fake\Two.exe", TestContext.Current.CancellationToken);
        await provider.GetMetadataAsync(@"C:\Fake\Three.exe", TestContext.Current.CancellationToken);

        Assert.Equal(3, reads);
        Assert.Equal(2, provider.CachedItemCount);
        Assert.Equal(2, provider.Capacity);
    }

    [Fact]
    public async Task DigitalSignatureInspectionCachesPositiveAndNegativeResults()
    {
        int inspections = 0;
        DigitalSignatureInspector inspector = new(
            _ =>
            {
                inspections++;
                return new DigitalSignatureInfo(
                    DigitalSignatureStatus.CertificateNotPresent,
                    null,
                    null,
                    null,
                    "unsigned test file");
            },
            capacity: 1);

        DigitalSignatureInfo first = await inspector.InspectAsync(
            @"C:\Fake\Unsigned.exe",
            TestContext.Current.CancellationToken);
        DigitalSignatureInfo second = await inspector.InspectAsync(
            @"C:\Fake\Unsigned.exe",
            TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(1, inspections);
        Assert.Equal(1, inspector.CachedItemCount);
    }

    [Fact]
    public async Task IconExtractionUsesRequestedSizeAndBoundedCache()
    {
        int extractions = 0;
        WindowsApplicationIconProvider provider = new(
            (path, size, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                extractions++;
                return ValueTask.FromResult<ApplicationIconData?>(new ApplicationIconData([1, 2, 3], "image/png", size));
            },
            capacity: 2);

        ApplicationIconData? first = await provider.GetIconAsync(
            @"C:\Fake\App.exe",
            32,
            TestContext.Current.CancellationToken);
        ApplicationIconData? cached = await provider.GetIconAsync(
            @"C:\Fake\App.exe",
            32,
            TestContext.Current.CancellationToken);
        await provider.GetIconAsync(@"C:\Fake\App.exe", 64, TestContext.Current.CancellationToken);

        Assert.Same(first, cached);
        Assert.Equal(2, extractions);
        Assert.Equal(32, first?.PixelSize);
        Assert.Equal(new byte[] { 1, 2, 3 }, first?.Content.ToArray());
        Assert.Equal(2, provider.CachedItemCount);
    }

    [Fact]
    public async Task Win32CatalogDeduplicatesBoundsAndCachesLoaderResult()
    {
        int loads = 0;
        InstalledApplicationCatalogEntry duplicate = Installed("win32:one", "One", @"C:\Apps\One");
        using Win32InstalledApplicationCatalog catalog = new(
            () =>
            {
                loads++;
                return
                [
                    duplicate,
                    duplicate with { DisplayName = "Duplicate" },
                    Installed("win32:two", "Two", @"C:\Apps\Two"),
                    Installed("win32:three", "Three", @"C:\Apps\Three")
                ];
            },
            capacity: 2,
            refreshInterval: TimeSpan.FromHours(1));

        IReadOnlyList<InstalledApplicationCatalogEntry> first = await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<InstalledApplicationCatalogEntry> second = await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.Select(item => item.CatalogId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Win32CatalogCachesAnEmptyLoaderResult()
    {
        int loads = 0;
        using Win32InstalledApplicationCatalog catalog = new(
            () =>
            {
                loads++;
                return [];
            },
            refreshInterval: TimeSpan.FromHours(1));

        Assert.Empty(await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, loads);
    }

    [Theory]
    [InlineData("\"C:\\Apps\\App.exe\",0", @"C:\Apps\App.exe")]
    [InlineData("C:\\Apps\\App.exe,-1", @"C:\Apps\App.exe")]
    public void Win32DisplayIconPathRemovesResourceIndex(string value, string expected)
    {
        Assert.Equal(expected, Win32InstalledApplicationCatalog.NormalizeDisplayIconPath(value));
    }

    [Fact]
    public async Task MsixCatalogDeduplicatesBoundsAndCachesLoaderResult()
    {
        int loads = 0;
        PackageApplicationCatalogEntry duplicate = Package("msix:one", "Family.One", "Family.One!App");
        using MsixPackageApplicationCatalog catalog = new(
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                loads++;
                IReadOnlyList<PackageApplicationCatalogEntry> values =
                [
                    duplicate,
                    duplicate with { DisplayName = "Duplicate" },
                    Package("msix:two", "Family.Two", "Family.Two!App"),
                    Package("msix:three", "Family.Three", "Family.Three!App")
                ];
                return ValueTask.FromResult(values);
            },
            capacity: 2,
            refreshInterval: TimeSpan.FromHours(1));

        IReadOnlyList<PackageApplicationCatalogEntry> first = await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<PackageApplicationCatalogEntry> second = await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.Select(item => item.CatalogId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task MsixCatalogCachesAnEmptyLoaderResult()
    {
        int loads = 0;
        using MsixPackageApplicationCatalog catalog = new(
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                loads++;
                return ValueTask.FromResult<IReadOnlyList<PackageApplicationCatalogEntry>>([]);
            },
            refreshInterval: TimeSpan.FromHours(1));

        Assert.Empty(await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await catalog.GetApplicationsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task PackageIdentityResolverCachesNoPackageResultForStableProcessInstance()
    {
        using Process current = Process.GetCurrentProcess();
        ProcessDescriptor descriptor = new(
            new ProcessInstanceId(current.Id, new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero)),
            current.ProcessName,
            current.MainModule?.FileName,
            null,
            null,
            null,
            null,
            null,
            false,
            false);
        WindowsPackageIdentityResolver resolver = new(capacity: 4);

        PackageIdentity? first = await resolver.ResolveAsync(descriptor, TestContext.Current.CancellationToken);
        PackageIdentity? second = await resolver.ResolveAsync(descriptor, TestContext.Current.CancellationToken);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, resolver.CachedItemCount);
    }

    [Fact]
    public async Task NativeProcessDiscoveryPreservesCurrentPidAndStartTimeIdentity()
    {
        using Process current = Process.GetCurrentProcess();
        WindowsProcessDiscoveryService discovery = new(new ExecutableMetadataProvider());

        ProcessDiscoverySnapshot snapshot = await discovery.DiscoverAsync(TestContext.Current.CancellationToken);

        ProcessDescriptor discovered = Assert.Single(
            snapshot.Processes,
            item => item.InstanceId.ProcessId == current.Id);
        DateTimeOffset expected = new(current.StartTime.ToUniversalTime(), TimeSpan.Zero);
        Assert.Equal(expected, discovered.InstanceId.StartTimeUtc);
        Assert.Equal(current.ProcessName, discovered.NormalizedProcessName, ignoreCase: true);
    }

    private static ExecutableMetadata Metadata(string path) => new(
        path,
        "Product",
        "Description",
        "Company",
        "1.0",
        1,
        DateTimeOffset.UnixEpoch,
        true,
        null);

    private static InstalledApplicationCatalogEntry Installed(string id, string name, string location) => new(
        id,
        name,
        null,
        location,
        Path.Combine(location, $"{name}.exe"),
        null,
        "test");

    private static PackageApplicationCatalogEntry Package(string id, string family, string aumid) => new(
        id,
        family,
        $"{family}_1.0.0.0_x64__test",
        aumid,
        family,
        null,
        null,
        null,
        null);
}
