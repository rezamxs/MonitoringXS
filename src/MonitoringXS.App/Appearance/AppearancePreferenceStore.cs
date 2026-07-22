namespace MonitoringXS.App.Appearance;

public interface IAppearancePreferenceStore
{
    AppearanceMode Load();

    ValueTask<bool> SaveAsync(AppearanceMode mode, CancellationToken cancellationToken);
}

public sealed class FileAppearancePreferenceStore(string path) : IAppearancePreferenceStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppearanceMode Load()
    {
        try
        {
            string value = File.ReadAllText(path);
            return AppearancePreferenceSerializer.Parse(value.Trim());
        }
        catch (FileNotFoundException)
        {
            return AppearanceMode.System;
        }
        catch (DirectoryNotFoundException)
        {
            return AppearanceMode.System;
        }
        catch (IOException)
        {
            return AppearanceMode.System;
        }
        catch (UnauthorizedAccessException)
        {
            return AppearanceMode.System;
        }
    }

    public async ValueTask<bool> SaveAsync(AppearanceMode mode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                AppearancePreferenceSerializer.Serialize(mode),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
