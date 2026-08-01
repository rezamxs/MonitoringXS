using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Broker;

namespace MonitoringXS.Platform.Windows.Processes;

public sealed class WindowsProcessActionService : IProcessActionService, IDisposable
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TreeExitTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumTreePasses = 3;
    private readonly IProcessActionNative _native;
    private readonly Func<CancellationToken, ValueTask<BrokerGuard>> _brokerGuard;
    private readonly SemaphoreSlim _destructiveOperation = new(1, 1);

    public WindowsProcessActionService()
        : this(
            new WindowsProcessActionNative(),
            async cancellationToken =>
            {
                BrokerServiceSnapshot snapshot =
                    await BrokerServiceProbe.QueryAsync(cancellationToken);
                return snapshot.State == BrokerServiceState.Unknown
                    ? new(false, snapshot.ProcessId)
                    : new(true, snapshot.ProcessId);
            })
    {
    }

    internal WindowsProcessActionService(
        IProcessActionNative native,
        Func<CancellationToken, ValueTask<BrokerGuard>> brokerGuard)
    {
        _native = native;
        _brokerGuard = brokerGuard;
    }

    public async ValueTask<ProcessActionInspection> InspectAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken) =>
        await Task.Run(
            async () =>
            {
                ProcessValidation validation =
                    await ValidateAsync(target, cancellationToken);
                if (!validation.IsValid)
                {
                    return new ProcessActionInspection(
                        validation.Status,
                        validation.Message);
                }

                return new ProcessActionInspection(
                    ProcessActionStatus.Success,
                    "Process identity verified.",
                    validation.Process!.ExecutablePath);
            },
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProcessActionInspection> InspectTreeAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken) =>
        await Task.Run(
            async () =>
            {
                ProcessValidation validation =
                    await ValidateAsync(target, cancellationToken);
                if (!validation.IsValid)
                {
                    return new ProcessActionInspection(
                        validation.Status,
                        validation.Message);
                }

                TreeBuildResult tree = BuildTree(
                    target,
                    validation.BrokerProcessId);
                return tree.Status == ProcessActionStatus.Success
                    ? new ProcessActionInspection(
                        ProcessActionStatus.Success,
                        "Process identity verified.",
                        validation.Process!.ExecutablePath,
                        tree.Descendants.Count)
                    : new ProcessActionInspection(
                        tree.Status,
                        tree.Message,
                        validation.Process!.ExecutablePath);
            },
            cancellationToken).ConfigureAwait(false);

    public void Dispose() => _destructiveOperation.Dispose();

    public async ValueTask<ProcessActionResult> EndProcessAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(ProcessActionStatus.Cancelled, "Process action was cancelled.");
        }

        if (!await _destructiveOperation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy();
        }

        try
        {
            return await Task.Run(
                async () =>
                {
                    try
                    {
                        ProcessValidation validation =
                            await ValidateAsync(target, cancellationToken);
                        if (!validation.IsValid)
                        {
                            return Result(validation);
                        }

                        ProcessActionStatus status = _native.TerminateAndWait(
                            target,
                            ProcessExitTimeout,
                            cancellationToken);
                        return ResultForTermination(status, target.DisplayName);
                    }
                    catch (OperationCanceledException)
                    {
                        return new ProcessActionResult(
                            ProcessActionStatus.Cancelled,
                            "Process action was cancelled.");
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _destructiveOperation.Release();
        }
    }

    public async ValueTask<ProcessActionResult> EndProcessTreeAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(ProcessActionStatus.Cancelled, "Process tree action was cancelled.");
        }

        if (!await _destructiveOperation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy();
        }

        try
        {
            return await Task.Run(
                async () => await EndTreeCoreAsync(target, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _destructiveOperation.Release();
        }
    }

    public async ValueTask<ProcessActionResult> OpenFileLocationAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(ProcessActionStatus.Cancelled, "Process action was cancelled.");
        }

        return await Task.Run(
            async () =>
            {
                ProcessValidation validation =
                    await ValidateAsync(target, cancellationToken);
                if (!validation.IsValid)
                {
                    return Result(validation);
                }

                string? path = validation.Process!.ExecutablePath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return new(
                        ProcessActionStatus.ExecutablePathUnavailable,
                        "Executable path is unavailable or no longer exists.");
                }

                ProcessActionStatus status = _native.OpenExplorerSelecting(path);
                return status == ProcessActionStatus.Success
                    ? new(status, "File location opened.")
                    : new(status, SafeMessage(status));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProcessActionResult> EndTreeCoreAsync(
        ProcessActionTarget root,
        CancellationToken cancellationToken)
    {
        int terminated = 0;
        int failed = 0;
        HashSet<ProcessInstanceId> completed = [];
        Stopwatch elapsed = Stopwatch.StartNew();

        try
        {
            ProcessValidation rootValidation = await ValidateAsync(root, cancellationToken);
            if (!rootValidation.IsValid)
            {
                return Result(rootValidation);
            }

            for (int pass = 0; pass < MaximumTreePasses; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TreeBuildResult tree = BuildTree(
                    root,
                    rootValidation.BrokerProcessId);
                if (tree.Status != ProcessActionStatus.Success)
                {
                    return terminated == 0
                        ? new(tree.Status, tree.Message)
                        : Partial(terminated, failed + 1, tree.Message);
                }

                ProcessTreeTarget[] pending = tree.Descendants
                    .Where(item => !completed.Contains(item.Target.InstanceId))
                    .OrderByDescending(item => item.Depth)
                    .ToArray();
                if (pending.Length == 0)
                {
                    break;
                }

                foreach (ProcessTreeTarget child in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TimeSpan remaining = TreeExitTimeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return Partial(
                            terminated,
                            failed + pending.Length,
                            "Process tree termination timed out.");
                    }

                    ProcessActionStatus status = _native.TerminateAndWait(
                        child.Target,
                        Min(ProcessExitTimeout, remaining),
                        cancellationToken);
                    completed.Add(child.Target.InstanceId);
                    if (status == ProcessActionStatus.Success)
                    {
                        terminated++;
                    }
                    else if (status != ProcessActionStatus.AlreadyExited)
                    {
                        failed++;
                    }
                }
            }

            TreeBuildResult finalTree = BuildTree(
                root,
                rootValidation.BrokerProcessId);
            int remainingChildren = finalTree.Status == ProcessActionStatus.Success
                ? finalTree.Descendants.Count(item => !completed.Contains(item.Target.InstanceId))
                : 1;
            failed += remainingChildren;

            TimeSpan rootRemaining = TreeExitTimeout - elapsed.Elapsed;
            if (rootRemaining <= TimeSpan.Zero)
            {
                return Partial(
                    terminated,
                    failed + 1,
                    "Process tree termination timed out before the root exited.");
            }

            ProcessActionStatus rootStatus = _native.TerminateAndWait(
                root,
                Min(ProcessExitTimeout, rootRemaining),
                cancellationToken);
            if (rootStatus == ProcessActionStatus.Success)
            {
                terminated++;
            }
            else if (rootStatus != ProcessActionStatus.AlreadyExited)
            {
                failed++;
            }

            return failed == 0
                ? new(
                    ProcessActionStatus.Success,
                    $"{terminated} process{(terminated == 1 ? string.Empty : "es")} ended.",
                    terminated)
                : Partial(
                    terminated,
                    failed,
                    $"{terminated} process{(terminated == 1 ? string.Empty : "es")} ended; {failed} could not be ended.");
        }
        catch (OperationCanceledException)
        {
            return terminated == 0
                ? new(ProcessActionStatus.Cancelled, "Process tree action was cancelled.")
                : Partial(
                    terminated,
                    failed + 1,
                    "Process tree action was cancelled after partial completion.");
        }
    }

    private async ValueTask<ProcessValidation> ValidateAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.InstanceId.ProcessId is 0 or 4
            || string.IsNullOrWhiteSpace(target.ProcessName)
            || target.InstanceId.ProcessId < 0)
        {
            return Invalid("System and invalid targets are refused.");
        }

        if (target.InstanceId.ProcessId == Environment.ProcessId)
        {
            return Protected("Monitoring XS cannot act on itself.");
        }

        BrokerGuard broker = await _brokerGuard(cancellationToken);
        if (!broker.IsVerified)
        {
            return new(
                ProcessActionStatus.Failed,
                "Broker identity could not be verified; action refused.");
        }

        if (broker.ProcessId == target.InstanceId.ProcessId)
        {
            return Protected("Monitoring XS Broker actions are refused.");
        }

        NativeProcessRead read = _native.Read(target.InstanceId.ProcessId);
        if (read.Status != ProcessActionStatus.Success || read.Process is null)
        {
            return new(read.Status, SafeMessage(read.Status));
        }

        NativeProcessInfo process = read.Process;
        if (process.InstanceId != target.InstanceId
            || !string.Equals(process.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)
            || target.ExpectedExecutablePath is not null
                && !PathsEqual(process.ExecutablePath, target.ExpectedExecutablePath))
        {
            return new(
                ProcessActionStatus.StaleProcessIdentity,
                "Process identity changed; action refused.");
        }

        if (!process.SafetyVerified)
        {
            return new(
                ProcessActionStatus.AccessDenied,
                "Process safety could not be verified; action refused.");
        }

        if (process.IsCritical || process.IsProtected)
        {
            return Protected("Critical or protected Windows processes are refused.");
        }

        return new(
            ProcessActionStatus.Success,
            "Process identity verified.",
            process,
            broker.ProcessId);
    }

    private TreeBuildResult BuildTree(
        ProcessActionTarget root,
        int? brokerProcessId)
    {
        IReadOnlyList<NativeProcessTreeEntry> snapshot = _native.SnapshotTree();
        Dictionary<int, List<NativeProcessTreeEntry>> children = snapshot
            .Where(item => item.ParentProcessId is not null)
            .GroupBy(item => item.ParentProcessId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        Queue<(ProcessActionTarget Parent, int Depth)> pending = new();
        List<ProcessTreeTarget> descendants = [];
        HashSet<int> visited = [root.InstanceId.ProcessId];
        pending.Enqueue((root, 0));

        while (pending.TryDequeue(out (ProcessActionTarget Parent, int Depth) current))
        {
            if (!children.TryGetValue(current.Parent.InstanceId.ProcessId, out List<NativeProcessTreeEntry>? direct))
            {
                continue;
            }

            foreach (NativeProcessTreeEntry entry in direct)
            {
                if (!visited.Add(entry.ProcessId))
                {
                    continue;
                }

                if (entry.ProcessId == Environment.ProcessId
                    || entry.ProcessId == brokerProcessId)
                {
                    return new(
                        ProcessActionStatus.ProtectedProcess,
                        "Tree contains Monitoring XS or its Broker; action refused.",
                        descendants);
                }

                NativeProcessRead read = _native.Read(entry.ProcessId);
                if (read.Status == ProcessActionStatus.AlreadyExited)
                {
                    continue;
                }

                if (read.Status != ProcessActionStatus.Success || read.Process is null)
                {
                    return new(
                        read.Status,
                        "A descendant could not be safely identified; no new tree action was taken.",
                        descendants);
                }

                NativeProcessInfo process = read.Process;
                if (!process.SafetyVerified || process.IsCritical || process.IsProtected)
                {
                    return new(
                        ProcessActionStatus.ProtectedProcess,
                        "Tree contains an unverifiable, critical, or protected process; action refused.",
                        descendants);
                }

                if (process.InstanceId.StartTimeUtc < current.Parent.InstanceId.StartTimeUtc
                    || !string.Equals(
                        process.ProcessName,
                        entry.ProcessName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ProcessActionTarget child = new(
                    process.InstanceId,
                    process.ProcessName,
                    process.ProcessName,
                    process.ExecutablePath);
                descendants.Add(new(child, current.Depth + 1));
                pending.Enqueue((child, current.Depth + 1));
            }
        }

        return new(
            ProcessActionStatus.Success,
            "Process tree verified.",
            descendants);
    }

    private static bool PathsEqual(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(actual),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ProcessActionResult Result(ProcessValidation validation) =>
        new(validation.Status, validation.Message);

    private static ProcessActionResult ResultForTermination(
        ProcessActionStatus status,
        string displayName) => status switch
        {
            ProcessActionStatus.Success => new(status, $"{displayName} ended.", 1),
            ProcessActionStatus.AlreadyExited => new(status, "Process already exited."),
            _ => new(status, SafeMessage(status))
        };

    private static ProcessActionResult Busy() =>
        new(ProcessActionStatus.Failed, "Another process action is already running.");

    private static ProcessActionResult Partial(int terminated, int failed, string message) =>
        new(ProcessActionStatus.PartialTreeTermination, message, terminated, failed);

    private static ProcessValidation Invalid(string message) =>
        new(ProcessActionStatus.InvalidTarget, message);

    private static ProcessValidation Protected(string message) =>
        new(ProcessActionStatus.ProtectedProcess, message);

    private static string SafeMessage(ProcessActionStatus status) => status switch
    {
        ProcessActionStatus.AlreadyExited => "Process already exited.",
        ProcessActionStatus.StaleProcessIdentity => "Process identity changed; action refused.",
        ProcessActionStatus.AccessDenied => "Access denied. Monitoring XS remains non-elevated.",
        ProcessActionStatus.ProtectedProcess => "Critical or protected process action refused.",
        ProcessActionStatus.InvalidTarget => "Invalid process target.",
        ProcessActionStatus.ExecutablePathUnavailable => "Executable path is unavailable.",
        ProcessActionStatus.OperationTimedOut => "Process did not exit before the timeout.",
        ProcessActionStatus.Cancelled => "Process action was cancelled.",
        _ => "Process action failed."
    };

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    internal readonly record struct BrokerGuard(bool IsVerified, int? ProcessId);

    private sealed record ProcessValidation(
        ProcessActionStatus Status,
        string Message,
        NativeProcessInfo? Process = null,
        int? BrokerProcessId = null)
    {
        public bool IsValid => Status == ProcessActionStatus.Success;
    }

    private sealed record TreeBuildResult(
        ProcessActionStatus Status,
        string Message,
        IReadOnlyList<ProcessTreeTarget> Descendants);

    private sealed record ProcessTreeTarget(ProcessActionTarget Target, int Depth);
}

internal interface IProcessActionNative
{
    NativeProcessRead Read(int processId);

    IReadOnlyList<NativeProcessTreeEntry> SnapshotTree();

    ProcessActionStatus TerminateAndWait(
        ProcessActionTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ProcessActionStatus OpenExplorerSelecting(string executablePath);
}

internal sealed record NativeProcessInfo(
    ProcessInstanceId InstanceId,
    string ProcessName,
    string? ExecutablePath,
    bool SafetyVerified,
    bool IsCritical,
    bool IsProtected);

internal sealed record NativeProcessRead(
    ProcessActionStatus Status,
    NativeProcessInfo? Process = null);

internal sealed record NativeProcessTreeEntry(
    int ProcessId,
    int? ParentProcessId,
    string ProcessName);

internal sealed class WindowsProcessActionNative : IProcessActionNative
{
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProtectionLevelNone = 0xFFFFFFFE;
    private const int ProcessProtectionLevelInfo = 7;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int MaximumPathCharacters = 32_768;

    public NativeProcessRead Read(int processId)
    {
        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (process.IsInvalid)
        {
            return new(MapOpenError(Marshal.GetLastPInvokeError()));
        }

        return Read(process, processId);
    }

    public IReadOnlyList<NativeProcessTreeEntry> SnapshotTree() =>
        NativeProcessTree.Snapshot()
            .Select(item => new NativeProcessTreeEntry(
                item.ProcessId,
                item.ParentProcessId,
                NormalizeName(item.ExecutableName)))
            .ToArray();

    public ProcessActionStatus TerminateAndWait(
        ProcessActionTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SafeProcessHandle process = OpenProcess(
            ProcessTerminate | Synchronize | ProcessQueryLimitedInformation,
            false,
            target.InstanceId.ProcessId);
        if (process.IsInvalid)
        {
            return MapOpenError(Marshal.GetLastPInvokeError());
        }

        NativeProcessRead read = Read(process, target.InstanceId.ProcessId);
        if (read.Status != ProcessActionStatus.Success || read.Process is null)
        {
            return read.Status;
        }

        NativeProcessInfo actual = read.Process;
        if (actual.InstanceId != target.InstanceId
            || !string.Equals(actual.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)
            || target.ExpectedExecutablePath is not null
                && !PathEquals(actual.ExecutablePath, target.ExpectedExecutablePath))
        {
            return ProcessActionStatus.StaleProcessIdentity;
        }

        if (!actual.SafetyVerified || actual.IsCritical || actual.IsProtected)
        {
            return ProcessActionStatus.ProtectedProcess;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TerminateProcess(process, 1))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ErrorAccessDenied
                ? ProcessActionStatus.AccessDenied
                : ProcessActionStatus.Failed;
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            uint wait = WaitForSingleObject(process, 50);
            if (wait == WaitObject0)
            {
                return ProcessActionStatus.Success;
            }

            if (wait != WaitTimeout)
            {
                return ProcessActionStatus.Failed;
            }
        }

        return ProcessActionStatus.OperationTimedOut;
    }

    public ProcessActionStatus OpenExplorerSelecting(string executablePath)
    {
        try
        {
            ProcessStartInfo start = CreateExplorerStartInfo(executablePath);
            using Process? explorer = Process.Start(start);
            return explorer is null
                ? ProcessActionStatus.Failed
                : ProcessActionStatus.Success;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ProcessActionStatus.Failed;
        }
    }

    internal static ProcessStartInfo CreateExplorerStartInfo(string executablePath)
    {
        ProcessStartInfo start = new("explorer.exe")
        {
            UseShellExecute = false
        };
        start.ArgumentList.Add($"/select,{executablePath}");
        return start;
    }

    private static NativeProcessRead Read(SafeProcessHandle process, int processId)
    {
        if (!GetProcessTimes(process, out FileTime created, out _, out _, out _))
        {
            return new(MapOpenError(Marshal.GetLastPInvokeError()));
        }

        DateTimeOffset startTime;
        try
        {
            startTime = DateTimeOffset.FromFileTime(created.ToInt64()).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(ProcessActionStatus.InvalidTarget);
        }

        string? path = QueryExecutablePath(process);
        string? name = path is null
            ? NativeProcessTree.Snapshot()
                .FirstOrDefault(item => item.ProcessId == processId)?.ExecutableName
            : Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new(ProcessActionStatus.AccessDenied);
        }

        bool criticalCall = IsProcessCritical(process, out bool isCritical);
        bool protectionCall = GetProcessInformation(
            process,
            ProcessProtectionLevelInfo,
            out ProcessProtectionLevelInformation protection,
            (uint)Marshal.SizeOf<ProcessProtectionLevelInformation>());
        bool safetyVerified = criticalCall && protectionCall;
        return new(
            ProcessActionStatus.Success,
            new NativeProcessInfo(
                new ProcessInstanceId(processId, startTime),
                NormalizeName(name),
                path,
                safetyVerified,
                isCritical,
                protection.ProtectionLevel != ProtectionLevelNone));
    }

    private static unsafe string? QueryExecutablePath(SafeProcessHandle process)
    {
        const int commonCharacters = 1024;
        char* common = stackalloc char[commonCharacters];
        uint length = commonCharacters;
        if (QueryFullProcessImageName(process, 0, common, ref length) && length > 0)
        {
            return new string(common, 0, checked((int)length));
        }

        const int errorInsufficientBuffer = 122;
        if (Marshal.GetLastPInvokeError() != errorInsufficientBuffer)
        {
            return null;
        }

        char[] rented = ArrayPool<char>.Shared.Rent(MaximumPathCharacters);
        try
        {
            length = (uint)rented.Length;
            fixed (char* buffer = rented)
            {
                return QueryFullProcessImageName(process, 0, buffer, ref length) && length > 0
                    ? new string(buffer, 0, checked((int)length))
                    : null;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static string NormalizeName(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;

    private static bool PathEquals(string? actual, string expected) =>
        actual is not null
        && string.Equals(
            Path.GetFullPath(actual),
            Path.GetFullPath(expected),
            StringComparison.OrdinalIgnoreCase);

    private static ProcessActionStatus MapOpenError(int error) => error switch
    {
        ErrorAccessDenied => ProcessActionStatus.AccessDenied,
        ErrorInvalidParameter => ProcessActionStatus.AlreadyExited,
        _ => ProcessActionStatus.Failed
    };

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(
        SafeProcessHandle process,
        [MarshalAs(UnmanagedType.Bool)] out bool isCritical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        SafeProcessHandle process,
        int processInformationClass,
        out ProcessProtectionLevelInformation processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        char* executablePath,
        ref uint size);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public long ToInt64() =>
            unchecked((long)(((ulong)_highDateTime << 32) | _lowDateTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessProtectionLevelInformation
    {
        public uint ProtectionLevel;
    }
}
