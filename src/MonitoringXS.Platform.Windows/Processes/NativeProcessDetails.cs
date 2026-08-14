using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitoringXS.Platform.Windows.Processes;

internal static class NativeProcessDetails
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumPathCharacters = 32_768;

    public static ProcessDetailsReadResult Read(int processId, ProcessDetails? cached)
    {
        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (process.IsInvalid)
        {
            return ProcessDetailsReadResult.Failed(MapFailure(Marshal.GetLastPInvokeError()));
        }

        if (!GetProcessTimes(process, out FileTime creation, out _, out _, out _))
        {
            return ProcessDetailsReadResult.Failed(MapFailure(Marshal.GetLastPInvokeError()));
        }

        DateTimeOffset startTime;
        try
        {
            startTime = DateTimeOffset.FromFileTime(creation.ToInt64()).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return ProcessDetailsReadResult.Failed(ProcessDetailsReadFailure.Unavailable);
        }

        if (cached is not null && cached.StartTimeUtc == startTime)
        {
            return ProcessDetailsReadResult.Success(cached);
        }

        if (!ProcessIdToSessionId((uint)processId, out uint sessionId))
        {
            return ProcessDetailsReadResult.Failed(MapFailure(Marshal.GetLastPInvokeError()));
        }

        bool isServiceSession = sessionId == 0;
        ExecutablePathReadResult executablePath = isServiceSession
            ? new(null, ProcessDetailsReadFailure.None)
            : QueryExecutablePath(process);
        return ProcessDetailsReadResult.Success(new ProcessDetails(
            startTime,
            executablePath.Path,
            isServiceSession,
            executablePath.Failure));
    }

    private static unsafe ExecutablePathReadResult QueryExecutablePath(SafeProcessHandle process)
    {
        const int commonPathCharacters = 1024;
        char* commonPath = stackalloc char[commonPathCharacters];
        uint commonLength = commonPathCharacters;
        if (QueryFullProcessImageName(process, 0, commonPath, ref commonLength) && commonLength > 0)
        {
            return new(
                NullIfWhitespace(new string(commonPath, 0, checked((int)commonLength))),
                ProcessDetailsReadFailure.None);
        }

        const int errorInsufficientBuffer = 122;
        if (Marshal.GetLastPInvokeError() != errorInsufficientBuffer)
        {
            return new(null, MapFailure(Marshal.GetLastPInvokeError()));
        }

        char[] rented = ArrayPool<char>.Shared.Rent(MaximumPathCharacters);
        try
        {
            uint length = (uint)rented.Length;
            fixed (char* buffer = rented)
            {
                if (QueryFullProcessImageName(process, 0, buffer, ref length) && length > 0)
                {
                    return new(
                        NullIfWhitespace(new string(buffer, 0, checked((int)length))),
                        ProcessDetailsReadFailure.None);
                }

                return new(null, MapFailure(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProcessDetailsReadFailure MapFailure(int error) => error switch
    {
        ErrorAccessDenied => ProcessDetailsReadFailure.AccessDenied,
        ErrorInvalidHandle or ErrorInvalidParameter or ErrorNotFound => ProcessDetailsReadFailure.ProcessExited,
        _ => ProcessDetailsReadFailure.Unavailable
    };

    internal sealed record ProcessDetails(
        DateTimeOffset StartTimeUtc,
        string? ExecutablePath,
        bool IsServiceSession,
        ProcessDetailsReadFailure ExecutablePathFailure = ProcessDetailsReadFailure.None);

    internal readonly record struct ProcessDetailsReadResult(
        ProcessDetails? Details,
        ProcessDetailsReadFailure Failure)
    {
        public static ProcessDetailsReadResult Success(ProcessDetails details) =>
            new(details, ProcessDetailsReadFailure.None);

        public static ProcessDetailsReadResult Failed(ProcessDetailsReadFailure failure) =>
            new(null, failure);
    }

    internal readonly record struct ExecutablePathReadResult(
        string? Path,
        ProcessDetailsReadFailure Failure);

    internal enum ProcessDetailsReadFailure
    {
        None,
        AccessDenied,
        ProcessExited,
        Unavailable
    }

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
