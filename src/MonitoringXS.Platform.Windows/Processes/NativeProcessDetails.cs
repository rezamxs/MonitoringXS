using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MonitoringXS.Core.Models;

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
            return ProcessDetailsReadResult.Success(cached with { HandleCount = QueryHandleCount(process) });
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
            executablePath.Failure,
            QueryArchitecture(process),
            QueryHandleCount(process)));
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

    private static MetricValue<int> QueryHandleCount(SafeProcessHandle process)
    {
        if (GetProcessHandleCount(process, out uint count))
        {
            return MetricValue<int>.Available(count > int.MaxValue ? int.MaxValue : (int)count);
        }

        int error = Marshal.GetLastPInvokeError();
        return MetricValue<int>.Unavailable(
            error == ErrorAccessDenied ? MetricAvailability.AccessDenied : MetricAvailability.Unavailable,
            $"GetProcessHandleCount failed with Win32 error {error}.");
    }

    private static ProcessArchitecture QueryArchitecture(SafeProcessHandle process)
    {
        try
        {
            if (!IsWow64Process2(process, out ushort processMachine, out ushort nativeMachine))
            {
                return ProcessArchitecture.Unknown;
            }

            return MachineArchitecture(processMachine == 0 ? nativeMachine : processMachine);
        }
        catch (EntryPointNotFoundException)
        {
            return ProcessArchitecture.Unknown;
        }
    }

    private static ProcessArchitecture MachineArchitecture(ushort machine) => machine switch
    {
        0x014c => ProcessArchitecture.X86,
        0x8664 => ProcessArchitecture.X64,
        0xaa64 => ProcessArchitecture.Arm64,
        _ => ProcessArchitecture.Unknown
    };

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
        ProcessDetailsReadFailure ExecutablePathFailure = ProcessDetailsReadFailure.None,
        ProcessArchitecture Architecture = ProcessArchitecture.Unknown,
        MetricValue<int> HandleCount = default);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(SafeProcessHandle process, out uint handleCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        SafeProcessHandle process,
        out ushort processMachine,
        out ushort nativeMachine);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public long ToInt64() => unchecked((long)(((ulong)_highDateTime << 32) | _lowDateTime));
    }
}
