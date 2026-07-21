using System.Security.Cryptography;
using System.Text;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Attribution;

public sealed class ApplicationAttributionService : IApplicationAttributionService
{
    private static readonly HashSet<string> InfrastructureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "csrss", "wininit", "winlogon", "smss", "services",
        "svchost", "lsass", "fontdrvhost", "dwm", "sihost", "taskhostw", "runtimebroker",
        "searchindexer", "spoolsv", "audiodg", "wudfhost", "securityhealthservice"
    };

    private static readonly Dictionary<string, KnownApplication> KnownApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = new("google-chrome", "Google Chrome", "Google LLC"),
        ["msedge"] = new("microsoft-edge", "Microsoft Edge", "Microsoft Corporation"),
        ["code"] = new("visual-studio-code", "Visual Studio Code", "Microsoft Corporation"),
        ["devenv"] = new("visual-studio", "Visual Studio", "Microsoft Corporation"),
        ["discord"] = new("discord", "Discord", "Discord Inc."),
        ["spotify"] = new("spotify", "Spotify", "Spotify AB"),
        ["steam"] = new("steam", "Steam", "Valve Corporation"),
        ["epicgameslauncher"] = new("epic-games-launcher", "Epic Games Launcher", "Epic Games, Inc."),
        ["windowsterminal"] = new("windows-terminal", "Windows Terminal", "Microsoft Corporation"),
        ["wt"] = new("windows-terminal", "Windows Terminal", "Microsoft Corporation"),
        ["notepad"] = new("notepad", "Notepad", "Microsoft Corporation"),
        ["calculatorapp"] = new("calculator", "Calculator", "Microsoft Corporation"),
        ["calculator"] = new("calculator", "Calculator", "Microsoft Corporation"),
        ["powershell"] = new("powershell", "Windows PowerShell", "Microsoft Corporation"),
        ["pwsh"] = new("powershell", "PowerShell", "Microsoft Corporation")
    };

    private static readonly HashSet<string> VsCodeHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "git", "rg", "language_server", "code-tunnel", "electron"
    };

    private static readonly HashSet<string> BroadInstallRoots = new(
        GetBroadInstallRoots(),
        StringComparer.OrdinalIgnoreCase);

    private readonly IInstalledApplicationCatalog _installedCatalog;
    private readonly IPackageApplicationCatalog _packageCatalog;
    private readonly IPackageIdentityResolver _packageIdentityResolver;
    private readonly IDigitalSignatureInspector _signatureInspector;
    private readonly IUserAttributionOverrideStore _overrideStore;
    private readonly object _catalogCacheGate = new();
    private IReadOnlyList<InstalledApplicationCatalogEntry>? _installedSnapshot;
    private IReadOnlyList<PackageApplicationCatalogEntry>? _packageSnapshot;
    private CatalogMatcher? _catalogMatcher;

    public ApplicationAttributionService(
        IInstalledApplicationCatalog installedCatalog,
        IPackageApplicationCatalog packageCatalog,
        IPackageIdentityResolver packageIdentityResolver,
        IDigitalSignatureInspector signatureInspector,
        IUserAttributionOverrideStore overrideStore)
    {
        _installedCatalog = installedCatalog;
        _packageCatalog = packageCatalog;
        _packageIdentityResolver = packageIdentityResolver;
        _signatureInspector = signatureInspector;
        _overrideStore = overrideStore;
    }

    public async ValueTask<IReadOnlyList<AttributionResult>> AttributeAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);
        IReadOnlyList<InstalledApplicationCatalogEntry> installed =
            await _installedCatalog.GetApplicationsAsync(cancellationToken);
        IReadOnlyList<PackageApplicationCatalogEntry> packages =
            await _packageCatalog.GetApplicationsAsync(cancellationToken);
        UserAttributionOverrideSnapshot overrides = await _overrideStore.GetAllAsync(cancellationToken);
        CatalogMatcher catalogs = GetCatalogMatcher(installed, packages);
        Dictionary<int, ProcessDescriptor> byPid = processes
            .GroupBy(item => item.InstanceId.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());
        List<Candidate> candidates = new(processes.Count);

        foreach (ProcessDescriptor process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(await AttributeOneAsync(process, byPid, catalogs, overrides, cancellationToken));
        }

        HashSet<string> visibleApplicationIds = candidates
            .Where(item => item.Result.Application is not null
                && (item.Result.Process.HasVisibleWindow || !item.RequiresVisiblePeer))
            .Select(item => item.Result.Application!.LogicalApplicationId)
            .ToHashSet(StringComparer.Ordinal);

        return candidates.Select(candidate =>
        {
            if (!candidate.RequiresVisiblePeer
                || candidate.Result.Application is null
                || visibleApplicationIds.Contains(candidate.Result.Application.LogicalApplicationId))
            {
                return candidate.Result;
            }

            return AttributionResult.Hidden(
                candidate.Result.Process,
                "Background executable had no visible application peer; it is excluded from application totals.");
        }).ToArray();
    }

    private CatalogMatcher GetCatalogMatcher(
        IReadOnlyList<InstalledApplicationCatalogEntry> installed,
        IReadOnlyList<PackageApplicationCatalogEntry> packages)
    {
        lock (_catalogCacheGate)
        {
            if (_catalogMatcher is not null
                && ReferenceEquals(installed, _installedSnapshot)
                && ReferenceEquals(packages, _packageSnapshot))
            {
                return _catalogMatcher;
            }

            _installedSnapshot = installed;
            _packageSnapshot = packages;
            _catalogMatcher = new CatalogMatcher(installed, packages);
            return _catalogMatcher;
        }
    }

    private async ValueTask<Candidate> AttributeOneAsync(
        ProcessDescriptor process,
        IReadOnlyDictionary<int, ProcessDescriptor> byPid,
        CatalogMatcher catalogs,
        UserAttributionOverrideSnapshot overrides,
        CancellationToken cancellationToken)
    {
        string name = process.NormalizedProcessName;
        if (InfrastructureNames.Contains(name))
        {
            return Candidate.Final(AttributionResult.Hidden(process, "Known Windows infrastructure process."));
        }

        if (process.IsServiceSession)
        {
            return Candidate.Final(AttributionResult.Hidden(process, "Process belongs to the service session."));
        }

        if (TryGetOverride(process.ExecutablePath, overrides, out UserAttributionOverride? attributionOverride))
        {
            ApplicationIdentity overridden = new(
                attributionOverride!.LogicalApplicationId,
                attributionOverride.DisplayName,
                attributionOverride.Publisher,
                attributionOverride.Disposition,
                Path.GetDirectoryName(attributionOverride.ExecutablePath),
                ClassificationConfidence.Certain,
                "Explicit persistent user attribution override matched the executable path.");
            return Candidate.Final(AttributionResult.Attributed(process, overridden));
        }

        PackageIdentity? packageIdentity = await _packageIdentityResolver.ResolveAsync(process, cancellationToken);
        if (packageIdentity is not null)
        {
            PackageApplicationCatalogEntry? package = catalogs.FindPackage(packageIdentity);
            string logicalId = package?.CatalogId
                ?? $"msix:{(packageIdentity.ApplicationUserModelId ?? packageIdentity.PackageFamilyName).ToLowerInvariant()}";
            string displayName = package?.DisplayName
                ?? process.ProductName
                ?? process.FileDescription
                ?? process.NormalizedProcessName;
            ApplicationIdentity identity = new(
                logicalId,
                displayName,
                package?.Publisher ?? process.Publisher,
                ApplicationDisposition.Packaged,
                package?.InstallLocation ?? GetInstallationRoot(process.ExecutablePath),
                package is null ? ClassificationConfidence.High : ClassificationConfidence.Certain,
                package is null
                    ? "Windows package family identity matched; no app-list catalog entry was available."
                    : package.ApplicationUserModelId is not null
                        && package.ApplicationUserModelId.Equals(packageIdentity.ApplicationUserModelId, StringComparison.OrdinalIgnoreCase)
                            ? "Exact AppUserModelID and MSIX package catalog entry matched."
                            : "Package family identity and MSIX package catalog entry matched.");
            return Candidate.PeerRequired(AttributionResult.Attributed(process, identity), !process.HasVisibleWindow);
        }

        if (KnownApplications.TryGetValue(name, out KnownApplication? known))
        {
            InstalledApplicationCatalogEntry? registration = catalogs.FindInstalled(process.ExecutablePath);
            SignatureEvidence signature = await GetSignatureEvidenceAsync(process, cancellationToken);
            ApplicationIdentity identity = new(
                known.Id,
                known.DisplayName,
                registration?.Publisher ?? signature.SignerName ?? process.Publisher ?? known.Publisher,
                registration is null ? ApplicationDisposition.Portable : ApplicationDisposition.Installed,
                registration?.InstallLocation ?? GetInstallationRoot(process.ExecutablePath),
                registration is null ? ClassificationConfidence.High : ClassificationConfidence.Certain,
                registration is null
                    ? $"Exact known user-facing executable rule; no installed catalog match, so it remains portable/unregistered.{signature.ReasonSuffix}"
                    : $"Exact known executable rule and installed Win32 catalog evidence matched.{signature.ReasonSuffix}");
            return Candidate.PeerRequired(AttributionResult.Attributed(process, identity), !process.HasVisibleWindow);
        }

        if (IsGameExecutable(process))
        {
            InstalledApplicationCatalogEntry? registration = catalogs.FindInstalled(process.ExecutablePath);
            ApplicationIdentity game = CreatePathIdentity(
                process,
                registration is null ? ApplicationDisposition.Portable : ApplicationDisposition.Installed,
                registration?.InstallLocation,
                registration is null ? ClassificationConfidence.Medium : ClassificationConfidence.High,
                registration is null
                    ? "Game-library executable kept separate from its launcher; no separate installed catalog record matched."
                    : "Game-library executable kept separate from its launcher and backed by installed catalog evidence.");
            return Candidate.PeerRequired(AttributionResult.Attributed(process, game), !process.HasVisibleWindow);
        }

        (KnownApplication Application, ProcessDescriptor Ancestor)? ancestor = FindKnownAncestor(process, byPid);
        if (ancestor is not null
            && ancestor.Value.Application.Id == "visual-studio-code"
            && VsCodeHelpers.Contains(name))
        {
            InstalledApplicationCatalogEntry? registration = catalogs.FindInstalled(ancestor.Value.Ancestor.ExecutablePath)
                ?? catalogs.FindInstalled(process.ExecutablePath);
            ApplicationIdentity identity = new(
                ancestor.Value.Application.Id,
                ancestor.Value.Application.DisplayName,
                registration?.Publisher ?? ancestor.Value.Application.Publisher,
                registration is null ? ApplicationDisposition.Portable : ApplicationDisposition.Installed,
                registration?.InstallLocation ?? GetInstallationRoot(ancestor.Value.Ancestor.ExecutablePath),
                ClassificationConfidence.High,
                "Known development helper with direct process ancestry to Visual Studio Code.");
            return Candidate.PeerRequired(AttributionResult.Attributed(process, identity), requiresVisiblePeer: true);
        }

        if (IsWindowsSystemPath(process.ExecutablePath))
        {
            return Candidate.Final(AttributionResult.Hidden(
                process,
                "Executable is Windows infrastructure without a user-facing identity."));
        }

        InstalledApplicationCatalogEntry? installed = catalogs.FindInstalled(process.ExecutablePath);
        if (installed is not null)
        {
            SignatureEvidence signature = await GetSignatureEvidenceAsync(process, cancellationToken);
            bool exactExecutable = PathsEqual(process.ExecutablePath, installed.PrimaryExecutablePath);
            ApplicationIdentity identity = new(
                installed.CatalogId,
                installed.DisplayName,
                installed.Publisher ?? signature.SignerName ?? process.Publisher,
                ApplicationDisposition.Installed,
                installed.InstallLocation,
                exactExecutable ? ClassificationConfidence.Certain : ClassificationConfidence.High,
                exactExecutable
                    ? $"Executable path exactly matched the primary executable in the installed Win32 catalog.{signature.ReasonSuffix}"
                    : $"Executable path matched the most specific installed Win32 application directory.{signature.ReasonSuffix}");
            return Candidate.PeerRequired(AttributionResult.Attributed(process, identity), !process.HasVisibleWindow);
        }

        if (!process.HasVisibleWindow)
        {
            return Candidate.Final(AttributionResult.Hidden(
                process,
                "No visible window, explicit application rule, package identity, catalog peer, or user override was found."));
        }

        SignatureEvidence portableSignature = await GetSignatureEvidenceAsync(process, cancellationToken);
        ApplicationIdentity portable = CreatePathIdentity(
            process,
            ApplicationDisposition.Portable,
            GetInstallationRoot(process.ExecutablePath),
            ClassificationConfidence.Medium,
            $"Visible top-level window with executable identity; no Win32 or package catalog match, so it remains portable/unregistered.{portableSignature.ReasonSuffix}",
            portableSignature.SignerName ?? process.Publisher);
        return Candidate.Final(AttributionResult.Attributed(process, portable));
    }

    private async ValueTask<SignatureEvidence> GetSignatureEvidenceAsync(
        ProcessDescriptor process,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return SignatureEvidence.None;
        }

        DigitalSignatureInfo signature = await _signatureInspector.InspectAsync(
            process.ExecutablePath,
            cancellationToken);
        return signature.Status == DigitalSignatureStatus.CertificatePresent
            ? new SignatureEvidence(
                signature.SignerName,
                $" Embedded signer certificate: {signature.SignerName ?? "name unavailable"}; certificate presence does not imply trust validation.")
            : SignatureEvidence.None;
    }

    private static bool TryGetOverride(
        string? executablePath,
        UserAttributionOverrideSnapshot snapshot,
        out UserAttributionOverride? attributionOverride)
    {
        attributionOverride = null;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            return snapshot.Overrides.TryGetValue(Path.GetFullPath(executablePath), out attributionOverride);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ApplicationIdentity CreatePathIdentity(
        ProcessDescriptor process,
        ApplicationDisposition disposition,
        string? installationPath,
        ClassificationConfidence confidence,
        string reason,
        string? publisher = null)
    {
        string displayName = process.ProductName ?? process.FileDescription ?? process.NormalizedProcessName;
        string stablePart = process.ExecutablePath?.ToUpperInvariant() ?? process.NormalizedProcessName.ToUpperInvariant();
        string logicalId = $"exe:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stablePart)))[..16].ToLowerInvariant()}";
        return new ApplicationIdentity(
            logicalId,
            displayName,
            publisher ?? process.Publisher,
            disposition,
            installationPath,
            confidence,
            reason);
    }

    private static (KnownApplication Application, ProcessDescriptor Ancestor)? FindKnownAncestor(
        ProcessDescriptor process,
        IReadOnlyDictionary<int, ProcessDescriptor> byPid)
    {
        int? current = process.ParentProcessId;
        for (int depth = 0; depth < 6 && current.HasValue; depth++)
        {
            if (!byPid.TryGetValue(current.Value, out ProcessDescriptor? parent))
            {
                break;
            }

            if (KnownApplications.TryGetValue(parent.NormalizedProcessName, out KnownApplication? known))
            {
                return (known, parent);
            }

            current = parent.ParentProcessId;
        }

        return null;
    }

    private static bool IsGameExecutable(ProcessDescriptor process)
    {
        string path = process.ExecutablePath ?? string.Empty;
        return path.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase)
                && !process.NormalizedProcessName.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    private static bool IsUnder(string path, string? root)
    {
        string? normalizedPath = NormalizePath(path);
        string? normalizedRoot = NormalizePath(root);
        if (normalizedPath is null || normalizedRoot is null)
        {
            return false;
        }

        return IsUnderNormalized(normalizedPath, normalizedRoot);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        string? normalizedLeft = NormalizePath(left);
        string? normalizedRight = NormalizePath(right);
        return normalizedLeft is not null
            && normalizedRight is not null
            && normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderNormalized(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> GetBroadInstallRoots()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData
        ];
        foreach (Environment.SpecialFolder folder in folders)
        {
            string? path = NormalizePath(Environment.GetFolderPath(folder));
            if (path is not null)
            {
                yield return path;
            }
        }
    }

    private static string? GetInstallationRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);

    private sealed class CatalogMatcher
    {
        private readonly Dictionary<string, InstalledApplicationCatalogEntry> _installedByExecutable;
        private readonly (string Root, InstalledApplicationCatalogEntry Entry)[] _installedByRoot;
        private readonly Dictionary<string, PackageApplicationCatalogEntry> _packagesByAumid;
        private readonly Dictionary<string, PackageApplicationCatalogEntry> _packagesByFamily;

        public CatalogMatcher(
            IReadOnlyList<InstalledApplicationCatalogEntry> installed,
            IReadOnlyList<PackageApplicationCatalogEntry> packages)
        {
            _installedByExecutable = installed
                .Select(entry => (Entry: entry, Path: NormalizePath(entry.PrimaryExecutablePath)))
                .Where(item => item.Path is not null)
                .GroupBy(item => item.Path!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Entry, StringComparer.OrdinalIgnoreCase);
            _installedByRoot = installed
                .Select(entry => (Entry: entry, Root: NormalizePath(entry.InstallLocation)))
                .Where(item => item.Root is not null
                    && !BroadInstallRoots.Contains(item.Root)
                    && !item.Root.Equals(Path.GetPathRoot(item.Root), StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => item.Root!, StringComparer.OrdinalIgnoreCase)
                .Select(group => (group.Key, group.First().Entry))
                .OrderByDescending(item => item.Key.Length)
                .ToArray();
            _packagesByAumid = packages
                .Where(item => !string.IsNullOrWhiteSpace(item.ApplicationUserModelId))
                .GroupBy(item => item.ApplicationUserModelId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            _packagesByFamily = packages
                .GroupBy(item => item.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        public InstalledApplicationCatalogEntry? FindInstalled(string? executablePath)
        {
            string? normalizedPath = NormalizePath(executablePath);
            if (normalizedPath is null)
            {
                return null;
            }

            if (_installedByExecutable.TryGetValue(normalizedPath, out InstalledApplicationCatalogEntry? exact))
            {
                return exact;
            }

            foreach ((string root, InstalledApplicationCatalogEntry entry) in _installedByRoot)
            {
                if (IsUnderNormalized(normalizedPath, root))
                {
                    return entry;
                }
            }

            return null;
        }

        public PackageApplicationCatalogEntry? FindPackage(PackageIdentity identity)
        {
            if (!string.IsNullOrWhiteSpace(identity.ApplicationUserModelId)
                && _packagesByAumid.TryGetValue(identity.ApplicationUserModelId, out PackageApplicationCatalogEntry? byAumid))
            {
                return byAumid;
            }

            return _packagesByFamily.GetValueOrDefault(identity.PackageFamilyName);
        }
    }

    private sealed record KnownApplication(string Id, string DisplayName, string Publisher);

    private sealed record Candidate(AttributionResult Result, bool RequiresVisiblePeer)
    {
        public static Candidate Final(AttributionResult result) => new(result, false);

        public static Candidate PeerRequired(AttributionResult result, bool requiresVisiblePeer) =>
            new(result, requiresVisiblePeer);
    }

    private sealed record SignatureEvidence(string? SignerName, string ReasonSuffix)
    {
        public static SignatureEvidence None { get; } = new(null, string.Empty);
    }
}
