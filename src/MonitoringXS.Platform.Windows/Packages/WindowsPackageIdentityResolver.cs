using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Caching;

namespace MonitoringXS.Platform.Windows.Packages;

public sealed class WindowsPackageIdentityResolver : IPackageIdentityResolver
{
    public const int DefaultCapacity = 2048;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    private readonly BoundedLruCache<ProcessInstanceId, PackageIdentity?> _cache;

    public WindowsPackageIdentityResolver()
        : this(DefaultCapacity)
    {
    }

    public WindowsPackageIdentityResolver(int capacity)
    {
        _cache = new BoundedLruCache<ProcessInstanceId, PackageIdentity?>(capacity);
    }

    public int CachedItemCount => _cache.Count;

    public int Capacity => _cache.Capacity;

    public ValueTask<PackageIdentity?> ResolveAsync(
        ProcessDescriptor process,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(process.InstanceId, out PackageIdentity? cached))
        {
            return ValueTask.FromResult(cached);
        }

        PackageIdentity? identity = Resolve(process);
        _cache.Set(process.InstanceId, identity);
        return ValueTask.FromResult(identity);
    }

    private static PackageIdentity? Resolve(ProcessDescriptor process)
    {
        using SafeProcessHandle handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)process.InstanceId.ProcessId));
        if (handle.IsInvalid || !IsSameProcessInstance(handle, process.InstanceId))
        {
            return null;
        }

        string? family = QueryPackageFamilyName(handle);
        if (family is null)
        {
            return null;
        }

        return new PackageIdentity(
            family,
            QueryPackageFullName(handle),
            QueryApplicationUserModelId(handle));
    }

    private static bool IsSameProcessInstance(SafeProcessHandle handle, ProcessInstanceId expected)
    {
        if (!GetProcessTimes(handle, out NativeFileTime created, out _, out _, out _))
        {
            return false;
        }

        long nativeTicks = ((long)created.HighDateTime << 32) | created.LowDateTime;
        long expectedTicks = expected.StartTimeUtc.UtcDateTime.ToFileTimeUtc();
        return Math.Abs(nativeTicks - expectedTicks) <= TimeSpan.FromMilliseconds(5).Ticks;
    }

    private static unsafe string? QueryPackageFamilyName(SafeProcessHandle handle)
    {
        uint length = 0;
        int result = GetPackageFamilyName(handle, ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        char[] buffer = new char[checked((int)length)];
        fixed (char* value = buffer)
        {
            return GetPackageFamilyName(handle, ref length, value) == 0
                ? ReadBuffer(buffer, length)
                : null;
        }
    }

    private static unsafe string? QueryPackageFullName(SafeProcessHandle handle)
    {
        uint length = 0;
        int result = GetPackageFullName(handle, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        char[] buffer = new char[checked((int)length)];
        fixed (char* value = buffer)
        {
            return GetPackageFullName(handle, ref length, value) == 0
                ? ReadBuffer(buffer, length)
                : null;
        }
    }

    private static unsafe string? QueryApplicationUserModelId(SafeProcessHandle handle)
    {
        uint length = 0;
        int result = GetApplicationUserModelId(handle, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        char[] buffer = new char[checked((int)length)];
        fixed (char* value = buffer)
        {
            return GetApplicationUserModelId(handle, ref length, value) == 0
                ? ReadBuffer(buffer, length)
                : null;
        }
    }

    private static string? ReadBuffer(char[] buffer, uint reportedLength)
    {
        int length = Math.Min(buffer.Length, checked((int)reportedLength));
        if (length > 0 && buffer[length - 1] == '\0')
        {
            length--;
        }

        return length == 0 ? null : NullIfWhitespace(new string(buffer, 0, length));
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetPackageFamilyName(
        SafeProcessHandle process,
        ref uint packageFamilyNameLength,
        char* packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetPackageFullName(
        SafeProcessHandle process,
        ref uint packageFullNameLength,
        char* packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetApplicationUserModelId(
        SafeProcessHandle process,
        ref uint applicationUserModelIdLength,
        char* applicationUserModelId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        public readonly uint LowDateTime;
        public readonly int HighDateTime;
    }
}
