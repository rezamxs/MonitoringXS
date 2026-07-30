using System.Text.Json;
using System.Text.Json.Serialization;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Storage.Settings;

public sealed class JsonApplicationSettingsStore : IApplicationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public JsonApplicationSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<ApplicationSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return new(ApplicationSettings.Default, true, false);
            }

            SettingsDocument? document;
            await using (FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                document = await JsonSerializer.DeserializeAsync<SettingsDocument>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            if (document?.Version > ApplicationSettings.CurrentVersion)
            {
                return new(
                    ApplicationSettings.Default,
                    false,
                    false,
                    "Settings were created by a newer application version.");
            }

            ApplicationSettings? settings = document is null
                ? null
                : new(
                    document.Version,
                    document.LiveSamplingSeconds,
                    document.HistoryRetentionHours,
                    document.Theme);
            if (settings?.IsValid == true)
            {
                return new(settings, true, false);
            }

            return RecoverInvalidFile("Invalid settings values were replaced with safe defaults.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return RecoverInvalidFile("Corrupt settings were replaced with safe defaults.");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(
                ApplicationSettings.Default,
                false,
                false,
                "Settings storage is unavailable.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ApplicationSettingsSaveResult> SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid)
        {
            return new(false, "Settings contain an unsupported value.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporaryPath = _path + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return new(false, "Settings storage is unavailable.");
            }

            Directory.CreateDirectory(directory);
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new SettingsDocument(
                        settings.Version,
                        settings.LiveSamplingSeconds,
                        settings.HistoryRetentionHours,
                        settings.Theme),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }

            return ApplicationSettingsSaveResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(false, "Settings storage is unavailable.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
            }

            _gate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
    }

    private ApplicationSettingsLoadResult RecoverInvalidFile(string error)
    {
        try
        {
            string quarantinePath =
                $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(_path, quarantinePath);
            return new(ApplicationSettings.Default, true, true, error);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(ApplicationSettings.Default, false, false, error);
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private sealed record SettingsDocument(
        int Version,
        int LiveSamplingSeconds,
        int HistoryRetentionHours,
        ApplicationTheme Theme);
}
