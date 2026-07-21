using System.Collections.ObjectModel;
using System.Text.Json;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Storage.Attribution;

public sealed class JsonUserAttributionOverrideStore : IUserAttributionOverrideStore, IDisposable
{
    public const int DefaultCapacity = 1024;
    private const int DocumentVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly int _capacity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, UserAttributionOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private string? _loadError;

    public JsonUserAttributionOverrideStore(string filePath, int capacity = DefaultCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _filePath = Path.GetFullPath(filePath);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public void Dispose() => _gate.Dispose();

    public async ValueTask<UserAttributionOverrideSnapshot> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<OverrideMutationResult> UpsertAsync(
        UserAttributionOverride attributionOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributionOverride);
        if (!TryNormalize(attributionOverride, out UserAttributionOverride? normalized, out string? validationError))
        {
            return OverrideMutationResult.Failure(validationError!);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (_loadError is not null)
            {
                return OverrideMutationResult.Failure(_loadError);
            }

            if (!_overrides.ContainsKey(normalized!.ExecutablePath) && _overrides.Count >= _capacity)
            {
                return OverrideMutationResult.Failure($"The attribution override limit of {_capacity} entries has been reached.");
            }

            Dictionary<string, UserAttributionOverride> updated = new(_overrides, StringComparer.OrdinalIgnoreCase)
            {
                [normalized.ExecutablePath] = normalized
            };
            OverrideMutationResult persisted = await PersistAsync(updated, cancellationToken);
            if (persisted.Succeeded)
            {
                _overrides = updated;
            }

            return persisted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<OverrideMutationResult> RemoveAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizePath(executablePath, out string? normalizedPath))
        {
            return OverrideMutationResult.Failure("The executable path is invalid.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (_loadError is not null)
            {
                return OverrideMutationResult.Failure(_loadError);
            }

            if (!_overrides.ContainsKey(normalizedPath!))
            {
                return OverrideMutationResult.Success;
            }

            Dictionary<string, UserAttributionOverride> updated = new(_overrides, StringComparer.OrdinalIgnoreCase);
            updated.Remove(normalizedPath!);
            OverrideMutationResult persisted = await PersistAsync(updated, cancellationToken);
            if (persisted.Succeeded)
            {
                _overrides = updated;
            }

            return persisted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            await using FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            OverrideDocument? document = await JsonSerializer.DeserializeAsync<OverrideDocument>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (document is null || document.Version != DocumentVersion)
            {
                _loadError = "The attribution override file has an unsupported format.";
                return;
            }

            Dictionary<string, UserAttributionOverride> loaded = new(StringComparer.OrdinalIgnoreCase);
            foreach (UserAttributionOverride entry in (document.Entries ?? []).Take(_capacity))
            {
                if (TryNormalize(entry, out UserAttributionOverride? normalized, out _))
                {
                    loaded[normalized!.ExecutablePath] = normalized;
                }
            }

            _overrides = loaded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _loaded = false;
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            _loadError = $"Attribution overrides are unavailable ({exception.GetType().Name}).";
        }
    }

    private async ValueTask<OverrideMutationResult> PersistAsync(
        Dictionary<string, UserAttributionOverride> updated,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return OverrideMutationResult.Failure("The attribution override location is invalid.");
        }

        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            OverrideDocument document = new(DocumentVersion, updated.Values
                .OrderBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .ToArray());
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            return OverrideMutationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return OverrideMutationResult.Failure($"Attribution overrides could not be saved ({exception.GetType().Name}).");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The next save uses a unique temporary name; stale temp files are harmless.
            }
        }
    }

    private UserAttributionOverrideSnapshot Snapshot()
    {
        Dictionary<string, UserAttributionOverride> copy = new(_overrides, StringComparer.OrdinalIgnoreCase);
        return new UserAttributionOverrideSnapshot(
            new ReadOnlyDictionary<string, UserAttributionOverride>(copy),
            _loadError is null,
            _loadError);
    }

    private static bool TryNormalize(
        UserAttributionOverride value,
        out UserAttributionOverride? normalized,
        out string? error)
    {
        normalized = null;
        if (!TryNormalizePath(value.ExecutablePath, out string? path))
        {
            error = "The executable path is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(value.LogicalApplicationId)
            || string.IsNullOrWhiteSpace(value.DisplayName)
            || value.Disposition is ApplicationDisposition.System or ApplicationDisposition.Unresolved)
        {
            error = "The override identity, display name, or disposition is invalid.";
            return false;
        }

        normalized = value with
        {
            ExecutablePath = path!,
            LogicalApplicationId = value.LogicalApplicationId.Trim(),
            DisplayName = value.DisplayName.Trim(),
            Publisher = string.IsNullOrWhiteSpace(value.Publisher) ? null : value.Publisher.Trim()
        };
        error = null;
        return true;
    }

    private static bool TryNormalizePath(string path, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return Path.IsPathFullyQualified(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record OverrideDocument(int Version, IReadOnlyList<UserAttributionOverride> Entries);
}
