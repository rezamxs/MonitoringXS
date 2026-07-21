using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Caching;

namespace MonitoringXS.Platform.Windows.Icons;

public sealed class WindowsApplicationIconProvider : IApplicationIconProvider
{
    public const int DefaultCapacity = 128;
    private const int MaximumIconBytes = 2 * 1024 * 1024;

    private readonly BoundedLruCache<IconCacheKey, ApplicationIconData?> _cache;
    private readonly Func<string, int, CancellationToken, ValueTask<ApplicationIconData?>> _extractor;

    public WindowsApplicationIconProvider()
        : this(ExtractAsync, DefaultCapacity)
    {
    }

    public WindowsApplicationIconProvider(
        Func<string, int, CancellationToken, ValueTask<ApplicationIconData?>> extractor,
        int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        _extractor = extractor;
        _cache = new BoundedLruCache<IconCacheKey, ApplicationIconData?>(capacity);
    }

    public int CachedItemCount => _cache.Count;

    public int Capacity => _cache.Capacity;

    public async ValueTask<ApplicationIconData?> GetIconAsync(
        string sourcePath,
        int pixelSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelSize, 512);

        IconCacheKey key = new(FileCacheKey.Create(sourcePath), pixelSize);
        if (_cache.TryGetValue(key, out ApplicationIconData? cached))
        {
            return cached;
        }

        ApplicationIconData? icon = await _extractor(sourcePath, pixelSize, cancellationToken);
        _cache.Set(key, icon);
        return icon;
    }

    private static async ValueTask<ApplicationIconData?> ExtractAsync(
        string sourcePath,
        int pixelSize,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(sourcePath));
            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                (uint)pixelSize,
                ThumbnailOptions.UseCurrentScale);
            if (thumbnail.Size is 0 or > MaximumIconBytes)
            {
                return null;
            }

            using DataReader reader = new(thumbnail.GetInputStreamAt(0));
            uint length = checked((uint)thumbnail.Size);
            uint loaded = await reader.LoadAsync(length);
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded != length)
            {
                return null;
            }

            byte[] bytes = new byte[length];
            reader.ReadBytes(bytes);
            string contentType = string.IsNullOrWhiteSpace(thumbnail.ContentType)
                ? "application/octet-stream"
                : thumbnail.ContentType;
            return new ApplicationIconData(bytes, contentType, pixelSize);
        }
        catch (Exception exception) when (exception is ArgumentException
            or UnauthorizedAccessException
            or FileNotFoundException
            or IOException
            or NotSupportedException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private readonly record struct IconCacheKey(FileCacheKey File, int PixelSize);
}
