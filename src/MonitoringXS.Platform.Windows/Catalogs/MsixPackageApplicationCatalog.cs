using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Catalogs;

public sealed class MsixPackageApplicationCatalog : IPackageApplicationCatalog, IDisposable
{
    public const int DefaultCapacity = 4096;
    private const int PackageLoadConcurrency = 8;
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<PackageApplicationCatalogEntry>>> _loader;
    private readonly int _capacity;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private PackageApplicationCatalogEntry[] _cached = [];
    private DateTimeOffset _expiresAt;
    private bool _hasCachedValue;

    public MsixPackageApplicationCatalog()
        : this(LoadPackagesAsync, DefaultCapacity, DefaultRefreshInterval)
    {
    }

    public MsixPackageApplicationCatalog(
        Func<CancellationToken, ValueTask<IReadOnlyList<PackageApplicationCatalogEntry>>> loader,
        int capacity = DefaultCapacity,
        TimeSpan? refreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _loader = loader;
        _capacity = capacity;
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
    }

    public int Capacity => _capacity;

    public void Dispose() => _refreshGate.Dispose();

    public async ValueTask<IReadOnlyList<PackageApplicationCatalogEntry>> GetApplicationsAsync(
        CancellationToken cancellationToken)
    {
        if (_hasCachedValue && DateTimeOffset.UtcNow < _expiresAt)
        {
            return _cached;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_hasCachedValue && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _cached;
            }

            IReadOnlyList<PackageApplicationCatalogEntry> loaded = await _loader(cancellationToken);
            _cached = loaded
                .Where(entry => !string.IsNullOrWhiteSpace(entry.PackageFamilyName)
                    && !string.IsNullOrWhiteSpace(entry.DisplayName))
                .GroupBy(entry => entry.CatalogId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(_capacity)
                .ToArray();
            _hasCachedValue = true;
            _expiresAt = DateTimeOffset.UtcNow.Add(_refreshInterval);
            return _cached;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async ValueTask<IReadOnlyList<PackageApplicationCatalogEntry>> LoadPackagesAsync(
        CancellationToken cancellationToken)
    {
        List<PackageApplicationCatalogEntry> results = [];
        PackageManager manager = new();
        IEnumerable<Package> packages;
        try
        {
            packages = manager.FindPackagesForUser(string.Empty)
                .Where(IsApplicationPackage)
                .ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            return results;
        }

        foreach (Package[] batch in packages.Chunk(PackageLoadConcurrency))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageApplicationCatalogEntry[][] entries = await Task.WhenAll(
                batch.Select(package => LoadPackageAsync(package, cancellationToken)));
            foreach (PackageApplicationCatalogEntry[] packageEntries in entries)
            {
                results.AddRange(packageEntries);
            }
        }

        return results;
    }

    private static async Task<PackageApplicationCatalogEntry[]> LoadPackageAsync(
        Package package,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AppListEntry> appEntries = await package.GetAppListEntriesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (appEntries.Count == 0)
            {
                return [];
            }

            string? installLocation = TryGetInstallLocation(package);
            return appEntries.Select(app =>
            {
                string? aumid = NullIfWhitespace(app.AppUserModelId);
                string displayName = NullIfWhitespace(app.DisplayInfo.DisplayName)
                    ?? NullIfWhitespace(package.DisplayName)
                    ?? package.Id.Name;
                string catalogId = $"msix:{(aumid ?? package.Id.FamilyName).ToLowerInvariant()}";
                return new PackageApplicationCatalogEntry(
                    catalogId,
                    package.Id.FamilyName,
                    package.Id.FullName,
                    aumid,
                    displayName,
                    NullIfWhitespace(package.PublisherDisplayName),
                    installLocation,
                    null,
                    null);
            }).ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or FileNotFoundException
            or System.Runtime.InteropServices.COMException)
        {
            // A package may disappear or become inaccessible during enumeration.
            return [];
        }
    }

    private static bool IsApplicationPackage(Package package)
    {
        try
        {
            return !package.IsFramework && !package.IsResourcePackage;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static string? TryGetInstallLocation(Package package)
    {
        try
        {
            return package.InstalledLocation?.Path;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or FileNotFoundException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
