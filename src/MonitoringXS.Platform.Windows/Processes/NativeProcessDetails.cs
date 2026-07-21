using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitoringXS.Platform.Windows.Processes;

internal static class NativeProcessDetails
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumPathCharacters = 32_768;

    public static ProcessDetails? TryRead(int processId, ProcessDetails? cached)
    {
        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (process.IsInvalid || !GetProcessTimes(process, out FileTime creation, out _, out _, out _))
        {
            return null;
        }

        DateTimeOffset startTime;
        try
        {
            startTime = DateTimeOffset.FromFileTime(creation.ToInt64());
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (cached is not null && cached.StartTimeUtc == startTime)
        {
            return cached;
        }

        if (!ProcessIdToSessionId((uint)processId, out uint sessionId))
        {
            return null;
        }

        bool isServiceSession = sessionId == 0;
        string? executablePath = isServiceSession ? null : QueryExecutablePath(process);
        return new ProcessDetails(startTime, executablePath, isServiceSession);
    }

    private static unsafe string? QueryExecutablePath(SafeProcessHandle process)
    {
        const int commonPathCharacters = 1024;
        char* commonPath = stackalloc char[commonPathCharacters];
        uint commonLength = commonPathCharacters;
        if (QueryFullProcessImageName(process, 0, commonPath, ref commonLength) && commonLength > 0)
        {
            return NullIfWhitespace(new string(commonPath, 0, checked((int)commonLength)));
        }

        const int errorInsufficientBuffer = 122;
        if (Marshal.GetLastPInvokeError() != errorInsufficientBuffer)
        {
            return null;
        }

        char[] rented = ArrayPool<char>.Shared.Rent(MaximumPathCharacters);
        try
        {
            uint length = (uint)rented.Length;
            fixed (char* buffer = rented)
            {
                return QueryFullProcessImageName(process, 0, buffer, ref length) && length > 0
                    ? NullIfWhitespace(new string(buffer, 0, checked((int)length)))
                    : null;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record ProcessDetails(
        DateTimeOffset StartTimeUtc,
        string? ExecutablePath,
        bool IsServiceSession);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        char* executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public long ToInt64() => unchecked((long)(((ulong)_highDateTime << 32) | _lowDateTime));
    }
}
