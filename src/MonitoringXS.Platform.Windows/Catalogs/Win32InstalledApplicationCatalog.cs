using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Catalogs;

public sealed class Win32InstalledApplicationCatalog : IInstalledApplicationCatalog, IDisposable
{
    public const int DefaultCapacity = 4096;
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(10);
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly Func<IEnumerable<InstalledApplicationCatalogEntry>> _loader;
    private readonly int _capacity;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private InstalledApplicationCatalogEntry[] _cached = [];
    private DateTimeOffset _expiresAt;
    private bool _hasCachedValue;

    public Win32InstalledApplicationCatalog()
        : this(LoadFromRegistry, DefaultCapacity, DefaultRefreshInterval)
    {
    }

    public Win32InstalledApplicationCatalog(
        Func<IEnumerable<InstalledApplicationCatalogEntry>> loader,
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

    public async ValueTask<IReadOnlyList<InstalledApplicationCatalogEntry>> GetApplicationsAsync(
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

            cancellationToken.ThrowIfCancellationRequested();
            _cached = _loader()
                .Where(IsUsable)
                .GroupBy(item => item.CatalogId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
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

    private static IEnumerable<InstalledApplicationCatalogEntry> LoadFromRegistry()
    {
        foreach ((RegistryHive hive, RegistryView view, string source) in Enumerations())
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstall = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string subkeyName in uninstall.GetSubKeyNames())
                {
                    InstalledApplicationCatalogEntry? entry = ReadEntry(uninstall, subkeyName, source);
                    if (entry is not null)
                    {
                        yield return entry;
                    }
                }
            }
            finally
            {
                uninstall?.Dispose();
                baseKey?.Dispose();
            }
        }
    }

    private static InstalledApplicationCatalogEntry? ReadEntry(
        RegistryKey uninstall,
        string subkeyName,
        string source)
    {
        try
        {
            using RegistryKey? key = uninstall.OpenSubKey(subkeyName, writable: false);
            if (key is null || ReadInt32(key, "SystemComponent") == 1 || IsUpdate(key))
            {
                return null;
            }

            string? displayName = ReadString(key, "DisplayName");
            if (displayName is null)
            {
                return null;
            }

            string? iconPath = NormalizeDisplayIconPath(ReadString(key, "DisplayIcon"));
            string? primaryExecutable = iconPath is not null
                && Path.GetExtension(iconPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? iconPath
                    : null;
            string? installLocation = NormalizeDirectory(ReadString(key, "InstallLocation"));
            installLocation ??= primaryExecutable is null ? null : Path.GetDirectoryName(primaryExecutable);
            string registrationSource = $"{source}:{subkeyName}";

            return new InstalledApplicationCatalogEntry(
                CreateCatalogId(displayName, ReadString(key, "Publisher"), installLocation, registrationSource),
                displayName,
                ReadString(key, "Publisher"),
                installLocation,
                primaryExecutable,
                iconPath,
                registrationSource);
        }
        catch (Exception exception) when (exception is SecurityException
            or UnauthorizedAccessException
            or IOException
            or ObjectDisposedException)
        {
            return null;
        }
    }

    public static string? NormalizeDisplayIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        string path;
        if (expanded.StartsWith('"'))
        {
            int closingQuote = expanded.IndexOf('"', 1);
            path = closingQuote > 1 ? expanded[1..closingQuote] : expanded.Trim('"');
        }
        else
        {
            int comma = expanded.LastIndexOf(',');
            path = comma > 0 && int.TryParse(expanded[(comma + 1)..], out _)
                ? expanded[..comma]
                : expanded;
        }

        path = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View, string Source)> Enumerations()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32");
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU64");
        yield return (RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU32");
    }

    private static bool IsUpdate(RegistryKey key)
    {
        string? releaseType = ReadString(key, "ReleaseType");
        return releaseType is not null
            && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("Security", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsable(InstalledApplicationCatalogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.CatalogId) && !string.IsNullOrWhiteSpace(entry.DisplayName);

    private static string? ReadString(RegistryKey key, string name) =>
        key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? ReadInt32(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int value => value,
        string value when int.TryParse(value, out int parsed) => parsed,
        _ => null
    };

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string CreateCatalogId(string name, string? publisher, string? location, string source)
    {
        string evidence = $"{name}\n{publisher}\n{location}\n{source}".ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)))[..20].ToLowerInvariant();
        return $"win32:{hash}";
    }
}
