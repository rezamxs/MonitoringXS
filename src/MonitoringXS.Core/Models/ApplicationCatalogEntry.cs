namespace MonitoringXS.Core.Models;

public sealed record InstalledApplicationCatalogEntry(
    string CatalogId,
    string DisplayName,
    string? Publisher,
    string? InstallLocation,
    string? PrimaryExecutablePath,
    string? IconSourcePath,
    string RegistrationSource);

public sealed record PackageApplicationCatalogEntry(
    string CatalogId,
    string PackageFamilyName,
    string PackageFullName,
    string? ApplicationUserModelId,
    string DisplayName,
    string? Publisher,
    string? InstallLocation,
    string? ExecutablePath,
    string? IconSourcePath);

public sealed record PackageIdentity(
    string PackageFamilyName,
    string? PackageFullName,
    string? ApplicationUserModelId);
