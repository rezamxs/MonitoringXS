using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
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
        // Root cause of blurry icons: the old paths returned a small
        // bitmap and the UI upscaled it. SHGetFileInfo with a 32px
        // request returned the 16px small icon (see ExtractShellIconAsync),
        // and a 32px bitmap shown at 32 logical pixels is upscaled again
        // on any display scale above 100%. Downscaling is sharp, upscaling
        // is not, so always fetch a larger bitmap than the display size
        // (at least 96px for a 32px display) and let the UI downscale it.
        // The image factory is tried first because it asks the Shell for
        // the icon at that larger size, straight from the largest icon
        // resource in the exe. Any failure here falls through to the old
        // paths, so a COM failure can only degrade to the previous
        // quality, never to a missing icon or a crash.
        ApplicationIconData? factoryIcon = await ExtractImageFactoryIconAsync(sourcePath, pixelSize);
        if (factoryIcon is not null)
        {
            return factoryIcon;
        }

        ApplicationIconData? thumbnail = await ExtractThumbnailAsync(sourcePath, pixelSize, cancellationToken);
        if (thumbnail is not null)
        {
            return thumbnail;
        }

        // Thumbnail is not an exe-icon API: it fails for many system and
        // service executables even when the exe has a real icon. Fall back
        // to the Shell icon for the same file, which is the native Windows
        // way to get an exe icon. Any failure here returns null, so the
        // caller still shows the fallback glyph instead of crashing.
        return await ExtractShellIconAsync(sourcePath, pixelSize);
    }

    private static async ValueTask<ApplicationIconData?> ExtractImageFactoryIconAsync(
        string sourcePath,
        int pixelSize)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30c1b64");
            int hr = SHCreateItemFromParsingName(
                Path.GetFullPath(sourcePath),
                IntPtr.Zero,
                ref iid,
                out factory);
            if (hr != 0 || factory is null)
            {
                return null;
            }

            int targetSize = Math.Min(256, Math.Max(96, pixelSize * 2));
            hr = factory.GetImage(
                new ShellImageSize(targetSize, targetSize),
                ShellImageFlags.IconOnly,
                out bitmap);
            if (hr != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            return await ConvertBitmapToPngAsync(bitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (factory is not null)
            {
                _ = Marshal.ReleaseComObject(factory);
            }
        }
    }

    private static async ValueTask<ApplicationIconData?> ExtractThumbnailAsync(
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


    private static async ValueTask<ApplicationIconData?> ExtractShellIconAsync(
        string sourcePath,
        int pixelSize)
    {
        IntPtr icon = IntPtr.Zero;
        IntPtr colorBitmap = IntPtr.Zero;
        IntPtr maskBitmap = IntPtr.Zero;
        try
        {
            // Bug that made Shell-fallback icons blurry: a 32px request
            // used to ask for the small icon, which is 16px, and the UI
            // then upscaled it 2x. The small icon is only correct for
            // a request of 16px or less; anything larger gets the large
            // (32px) icon, and the image-factory path above supplies
            // larger sizes when the exe has them.
            uint flags = ShellIconFlags.Icon | (pixelSize <= 16 ? ShellIconFlags.SmallIcon : ShellIconFlags.LargeIcon);
            ShellFileInfo info = new();
            IntPtr result = SHGetFileInfo(
                Path.GetFullPath(sourcePath),
                0,
                ref info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                flags);
            if (result == IntPtr.Zero || info.HIcon == IntPtr.Zero)
            {
                return null;
            }

            icon = info.HIcon;
            if (!GetIconInfo(icon, out IconInfo iconInfo))
            {
                return null;
            }

            colorBitmap = iconInfo.ColorBitmap;
            maskBitmap = iconInfo.MaskBitmap;
            if (colorBitmap == IntPtr.Zero)
            {
                return null;
            }

            return await ConvertBitmapToPngAsync(colorBitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (maskBitmap != IntPtr.Zero)
            {
                DeleteObject(maskBitmap);
            }

            if (colorBitmap != IntPtr.Zero)
            {
                DeleteObject(colorBitmap);
            }

            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }
        }
    }

    // Shared by the Shell icon and the image-factory paths: read the
    // bitmap pixels with GetDIBits and encode them as PNG. The returned
    // pixel size is the bitmap's real width, not the requested size, so
    // callers can tell a genuinely small icon from a high-res one.
    private static async ValueTask<ApplicationIconData?> ConvertBitmapToPngAsync(IntPtr bitmap)
    {
        IntPtr deviceContext = IntPtr.Zero;
        try
        {
            if (GetObject(bitmap, Marshal.SizeOf<BitmapStruct>(), out BitmapStruct info) == 0
                || info.Width <= 0 || info.Height <= 0
                || info.Width > 512 || info.Height > 512)
            {
                return null;
            }

            int width = info.Width;
            int height = info.Height;
            BitmapInfoHeader header = new()
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            };
            DibInfo dibInfo = new() { Header = header };
            byte[] pixels = new byte[width * height * 4];
            deviceContext = GetDC(IntPtr.Zero);
            if (deviceContext == IntPtr.Zero)
            {
                return null;
            }

            int lines = GetDIBits(deviceContext, bitmap, 0, (uint)height, pixels, ref dibInfo, 0);
            if (lines != height)
            {
                return null;
            }

            // Icons without an alpha channel report alpha 0 for every
            // pixel; rendering those as transparent would make the icon
            // invisible, so treat them as opaque. Modern 32-bit icons
            // carry real alpha and are left untouched.
            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha)
            {
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            using InMemoryRandomAccessStream stream = new();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                (uint)width,
                (uint)height,
                96,
                96,
                pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            using DataReader reader = new(stream.GetInputStreamAt(0));
            uint length = (uint)stream.Size;
            if (length is 0 or > MaximumIconBytes)
            {
                return null;
            }

            uint loaded = await reader.LoadAsync(length);
            if (loaded != length)
            {
                return null;
            }

            byte[] bytes = new byte[length];
            reader.ReadBytes(bytes);
            return new ApplicationIconData(bytes, "image/png", width);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (deviceContext != IntPtr.Zero
                && ReleaseDC(IntPtr.Zero, deviceContext) == 0)
            {
                // Best-effort cleanup: a failed ReleaseDC does not fail
                // icon extraction, which already produced its result or null.
            }
        }
    }

    private static class ShellIconFlags
    {
        public const uint SmallIcon = 0x1;
        public const uint LargeIcon = 0x0;
        public const uint Icon = 0x100;
    }

    private static class ShellImageFlags
    {
        // SIIGBF_ICONONLY: return only the icon, never a thumbnail.
        // SIIGBF_RESIZETOFIT is 0, so a smaller source icon is returned
        // at its real size instead of being upscaled by the Shell.
        public const uint IconOnly = 0x4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellImageSize
    {
        public int Width;
        public int Height;

        public ShellImageSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    // IShellItemImageFactory (shobjidl_core.h): inherits IUnknown and has
    // a single method, GetImage(SIZE, SIIGBF, out HBITMAP), returning an
    // HRESULT. PreserveSig keeps that HRESULT as the return value so
    // failure is checked explicitly instead of throwing.
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c1b64")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(ShellImageSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr HIcon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapStruct
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibInfo
    {
        public BitmapInfoHeader Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        out IShellItemImageFactory factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr bitmap, int size, out BitmapStruct bitmapInfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        byte[] pixels,
        ref DibInfo bitmapInfo,
        uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    private readonly record struct IconCacheKey(FileCacheKey File, int PixelSize);
}
