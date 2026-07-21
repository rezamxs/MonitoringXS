namespace MonitoringXS.Platform.Windows.Caching;

internal readonly record struct FileCacheKey(string NormalizedPath, long Length, long LastWriteTimeUtcTicks)
{
    public static FileCacheKey Create(string path)
    {
        string normalized = NormalizePath(path);
        try
        {
            FileInfo file = new(normalized);
            return file.Exists
                ? new FileCacheKey(normalized, file.Length, file.LastWriteTimeUtc.Ticks)
                : new FileCacheKey(normalized, -1, 0);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException
            or IOException)
        {
            return new FileCacheKey(normalized, -1, 0);
        }
    }

    public static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().ToUpperInvariant();
        }
    }
}
