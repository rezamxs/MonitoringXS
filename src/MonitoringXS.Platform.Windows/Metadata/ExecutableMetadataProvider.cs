using System.Diagnostics;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Caching;

namespace MonitoringXS.Platform.Windows.Metadata;

public sealed class ExecutableMetadataProvider : IExecutableMetadataProvider
{
    public const int DefaultCapacity = 512;

    private readonly BoundedLruCache<FileCacheKey, ExecutableMetadata> _cache;
    private readonly Func<string, ExecutableMetadata> _reader;

    public ExecutableMetadataProvider()
        : this(ReadMetadata, DefaultCapacity)
    {
    }

    public ExecutableMetadataProvider(Func<string, ExecutableMetadata> reader, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _cache = new BoundedLruCache<FileCacheKey, ExecutableMetadata>(capacity);
    }

    public int CachedItemCount => _cache.Count;

    public int Capacity => _cache.Capacity;

    public ValueTask<ExecutableMetadata> GetMetadataAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        FileCacheKey key = FileCacheKey.Create(executablePath);
        if (_cache.TryGetValue(key, out ExecutableMetadata? cached))
        {
            return ValueTask.FromResult(cached!);
        }

        ExecutableMetadata metadata = _reader(executablePath);
        _cache.Set(key, metadata);
        return ValueTask.FromResult(metadata);
    }

    private static ExecutableMetadata ReadMetadata(string path)
    {
        FileCacheKey identity = FileCacheKey.Create(path);
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            return new ExecutableMetadata(
                path,
                NullIfWhitespace(version.ProductName),
                NullIfWhitespace(version.FileDescription),
                NullIfWhitespace(version.CompanyName),
                NullIfWhitespace(version.FileVersion),
                Math.Max(0, identity.Length),
                identity.LastWriteTimeUtcTicks == 0
                    ? DateTimeOffset.MinValue
                    : new DateTimeOffset(identity.LastWriteTimeUtcTicks, TimeSpan.Zero),
                true,
                null);
        }
        catch (Exception exception) when (exception is ArgumentException
            or UnauthorizedAccessException
            or NotSupportedException
            or IOException
            or System.Security.SecurityException)
        {
            return new ExecutableMetadata(
                path,
                null,
                null,
                null,
                null,
                Math.Max(0, identity.Length),
                identity.LastWriteTimeUtcTicks == 0
                    ? DateTimeOffset.MinValue
                    : new DateTimeOffset(identity.LastWriteTimeUtcTicks, TimeSpan.Zero),
                false,
                exception.GetType().Name);
        }
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
