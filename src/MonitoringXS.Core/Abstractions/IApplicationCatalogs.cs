using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IInstalledApplicationCatalog
{
    ValueTask<IReadOnlyList<InstalledApplicationCatalogEntry>> GetApplicationsAsync(CancellationToken cancellationToken);
}

public interface IPackageApplicationCatalog
{
    ValueTask<IReadOnlyList<PackageApplicationCatalogEntry>> GetApplicationsAsync(CancellationToken cancellationToken);
}

public interface IPackageIdentityResolver
{
    ValueTask<PackageIdentity?> ResolveAsync(ProcessDescriptor process, CancellationToken cancellationToken);
}
