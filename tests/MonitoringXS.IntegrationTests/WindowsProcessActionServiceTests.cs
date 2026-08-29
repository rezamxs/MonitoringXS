using System.Diagnostics;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Processes;

namespace MonitoringXS.IntegrationTests;

public sealed class WindowsProcessActionServiceTests
{
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task IdentityValidationRejectsReusedPidAndExecutableMismatch()
    {
        FakeNative native = new();
        ProcessActionTarget target = Target(120, 10, "sample", @"C:\sample.exe");
        native.Processes[120] = Info(120, 11, "sample", @"C:\sample.exe");
        using WindowsProcessActionService service = Service(native);

        ProcessActionInspection staleStart =
            await service.InspectAsync(target, TestCancellation);
        native.Processes[120] = Info(120, 10, "other", @"C:\other.exe");
        ProcessActionInspection staleExecutable =
            await service.InspectAsync(target, TestCancellation);

        Assert.Equal(ProcessActionStatus.StaleProcessIdentity, staleStart.Status);
        Assert.Equal(ProcessActionStatus.StaleProcessIdentity, staleExecutable.Status);
        Assert.Empty(native.Terminated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task SystemAndInvalidTargetsAreRefused(int processId)
    {
        FakeNative native = new();
        ProcessActionTarget target = processId == 0
            ? new(
                default,
                "System",
                "System",
                null)
            : Target(processId, 1, "System");
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessAsync(target, TestCancellation);

        Assert.Contains(
            result.Status,
            new[] { ProcessActionStatus.InvalidTarget, ProcessActionStatus.ProtectedProcess });
        Assert.Empty(native.Terminated);
    }

    [Fact]
    public async Task SelfAndBrokerTargetsAreRefusedBeforeNativeTermination()
    {
        FakeNative native = new();
        ProcessActionTarget self = Target(Environment.ProcessId, 1, "MonitoringXS.App");
        ProcessActionTarget broker = Target(8123, 1, "MonitoringXS.PrivilegedBroker");
        using WindowsProcessActionService selfService = Service(native);
        using WindowsProcessActionService brokerService = Service(native, broker.InstanceId.ProcessId);

        ProcessActionResult selfResult =
            await selfService.EndProcessAsync(self, TestCancellation);
        ProcessActionResult brokerResult =
            await brokerService.EndProcessAsync(broker, TestCancellation);

        Assert.Equal(ProcessActionStatus.ProtectedProcess, selfResult.Status);
        Assert.Equal(ProcessActionStatus.ProtectedProcess, brokerResult.Status);
        Assert.Empty(native.Terminated);
    }

    [Theory]
    [InlineData(false, false, false, ProcessActionStatus.AccessDenied)]
    [InlineData(true, true, false, ProcessActionStatus.ProtectedProcess)]
    [InlineData(true, false, true, ProcessActionStatus.ProtectedProcess)]
    public async Task SafetyVerificationFailsClosed(
        bool verified,
        bool critical,
        bool protectedProcess,
        ProcessActionStatus expected)
    {
        FakeNative native = new();
        ProcessActionTarget target = Target(120, 10, "sample");
        native.Processes[120] = Info(
            120,
            10,
            "sample",
            verified: verified,
            critical: critical,
            protectedProcess: protectedProcess);
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessAsync(target, TestCancellation);

        Assert.Equal(expected, result.Status);
        Assert.Empty(native.Terminated);
    }

    [Theory]
    [InlineData(ProcessActionStatus.Success)]
    [InlineData(ProcessActionStatus.AlreadyExited)]
    [InlineData(ProcessActionStatus.AccessDenied)]
    [InlineData(ProcessActionStatus.OperationTimedOut)]
    public async Task EndTaskMapsNativeOutcomeAndConfirmsExit(
        ProcessActionStatus nativeStatus)
    {
        FakeNative native = ReadyNative();
        native.Termination = _ => nativeStatus;
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessAsync(Target(120, 10, "sample"), TestCancellation);

        Assert.Equal(nativeStatus, result.Status);
        Assert.Single(native.Terminated);
        Assert.Equal(nativeStatus == ProcessActionStatus.Success, result.IsSuccess);
    }

    [Fact]
    public async Task CancelledEndTaskReturnsTypedResultWithoutTermination()
    {
        FakeNative native = ReadyNative();
        using WindowsProcessActionService service = Service(native);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        ProcessActionResult result =
            await service.EndProcessAsync(Target(120, 10, "sample"), cancelled.Token);

        Assert.Equal(ProcessActionStatus.Cancelled, result.Status);
        Assert.Empty(native.Terminated);
    }

    [Fact]
    public async Task DuplicateDestructiveExecutionIsRejected()
    {
        FakeNative native = ReadyNative();
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        native.Termination = _ =>
        {
            started.Set();
            release.Wait(TestCancellation);
            return ProcessActionStatus.Success;
        };
        using WindowsProcessActionService service = Service(native);

        Task<ProcessActionResult> first =
            service.EndProcessAsync(Target(120, 10, "sample"), TestCancellation).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestCancellation));
        ProcessActionResult duplicate =
            await service.EndProcessAsync(Target(120, 10, "sample"), TestCancellation);
        release.Set();
        ProcessActionResult completed = await first;

        Assert.Equal(ProcessActionStatus.Failed, duplicate.Status);
        Assert.Contains("already running", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProcessActionStatus.Success, completed.Status);
        Assert.Single(native.Terminated);
    }

    [Fact]
    public async Task TreeTerminatesLeavesBeforeRoot()
    {
        FakeNative native = ReadyNative();
        native.Processes[121] = Info(121, 11, "child");
        native.Processes[122] = Info(122, 12, "grandchild");
        native.Tree =
        [
            new(121, 120, "child"),
            new(122, 121, "grandchild")
        ];
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessTreeAsync(Target(120, 10, "sample"), TestCancellation);

        Assert.Equal(ProcessActionStatus.Success, result.Status);
        Assert.Equal([122, 121, 120], native.Terminated);
        Assert.Equal(3, result.TerminatedCount);
    }

    [Fact]
    public async Task TreeHandlesDisappearingAndNewChildrenWithinBoundedPasses()
    {
        FakeNative native = ReadyNative();
        native.Processes[121] = Info(121, 11, "disappearing");
        native.Processes[122] = Info(122, 12, "newchild");
        int snapshots = 0;
        native.TreeFactory = () =>
        {
            snapshots++;
            if (snapshots == 1)
            {
                return [new(121, 120, "disappearing")];
            }

            return snapshots <= 3 ? [new(122, 120, "newchild")] : [];
        };
        native.Termination = target =>
        {
            native.Processes.Remove(target.InstanceId.ProcessId);
            return ProcessActionStatus.Success;
        };
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessTreeAsync(Target(120, 10, "sample"), TestCancellation);

        Assert.Equal(ProcessActionStatus.Success, result.Status);
        Assert.Contains(121, native.Terminated);
        Assert.Contains(122, native.Terminated);
        Assert.Equal(120, native.Terminated[^1]);
        Assert.InRange(snapshots, 2, 5);
    }

    [Fact]
    public async Task TreeSkipsChildThatDisappearsBeforeIdentityRead()
    {
        FakeNative native = ReadyNative();
        native.Tree = [new(121, 120, "gone")];
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessTreeAsync(Target(120, 10, "sample"), TestCancellation);

        Assert.Equal(ProcessActionStatus.Success, result.Status);
        Assert.Equal([120], native.Terminated);
    }

    [Fact]
    public async Task TreeContainingBrokerIsRefusedBeforeAnyTermination()
    {
        FakeNative native = ReadyNative();
        native.Processes[121] = Info(121, 11, "MonitoringXS.PrivilegedBroker");
        native.Tree = [new(121, 120, "MonitoringXS.PrivilegedBroker")];
        using WindowsProcessActionService service = Service(native, brokerProcessId: 121);

        ProcessActionResult result =
            await service.EndProcessTreeAsync(Target(120, 10, "sample"), TestCancellation);

        Assert.Equal(ProcessActionStatus.ProtectedProcess, result.Status);
        Assert.Empty(native.Terminated);
    }

    [Fact]
    public async Task StaleDescendantIsNeverMistakenForRelatedProcess()
    {
        FakeNative native = ReadyNative();
        native.Processes[121] = Info(121, 9, "unrelated");
        native.Tree = [new(121, 120, "oldchild")];
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessTreeAsync(Target(120, 10, "sample"), TestCancellation);

        Assert.Equal(ProcessActionStatus.Success, result.Status);
        Assert.Equal([120], native.Terminated);
    }

    [Fact]
    public async Task TreeReportsPartialFailurePrecisely()
    {
        FakeNative native = ReadyNative();
        native.Processes[121] = Info(121, 11, "child");
        native.Tree = [new(121, 120, "child")];
        native.Termination = target => target.InstanceId.ProcessId == 121
            ? ProcessActionStatus.AccessDenied
            : ProcessActionStatus.Success;
        using WindowsProcessActionService service = Service(native);

        ProcessActionResult result =
            await service.EndProcessTreeAsync(Target(120, 10, "sample"), TestCancellation);

        Assert.Equal(ProcessActionStatus.PartialTreeTermination, result.Status);
        Assert.Equal(1, result.TerminatedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal([121, 120], native.Terminated);
    }

    [Fact]
    public async Task OpenFileLocationRequiresExistingVerifiedPath()
    {
        string path = Path.GetTempFileName();
        try
        {
            FakeNative native = new();
            ProcessActionTarget target = Target(120, 10, "sample", path);
            native.Processes[120] = Info(120, 10, "sample", path);
            using WindowsProcessActionService service = Service(native);

            ProcessActionResult opened =
                await service.OpenFileLocationAsync(target, TestCancellation);
            File.Delete(path);
            ProcessActionResult deleted =
                await service.OpenFileLocationAsync(target, TestCancellation);

            Assert.Equal(ProcessActionStatus.Success, opened.Status);
            Assert.Equal(path, Assert.Single(native.OpenedPaths));
            Assert.Equal(ProcessActionStatus.ExecutablePathUnavailable, deleted.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExplorerArgumentsCannotBecomeShellCommands()
    {
        const string path = @"C:\safe folder\helper.exe & calc.exe";

        ProcessStartInfo start =
            WindowsProcessActionNative.CreateExplorerStartInfo(path);

        Assert.Equal("explorer.exe", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal([$"/select,{path}"], start.ArgumentList);
        Assert.DoesNotContain(
            start.Environment,
            item => item.Key.Contains("command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeImplementationUsesLimitedRightsAndNeverEnablesDebugPrivilege()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MonitoringXS.Platform.Windows",
            "Processes",
            "WindowsProcessActionService.cs"));

        Assert.Contains("ProcessQueryLimitedInformation", source, StringComparison.Ordinal);
        Assert.Contains("ProcessTerminate | Synchronize | ProcessQueryLimitedInformation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessAllAccess", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SeDebugPrivilege", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AdjustTokenPrivileges", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealDisposableHelperEndsOnlyAfterConfirmedExit()
    {
        string helper = HelperExecutable();
        using Process process = Process.Start(new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        try
        {
            ProcessActionTarget target = new(
                new(
                    process.Id,
                    new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)),
                "Process action test helper",
                "MonitoringXS.ProcessActionTestHelper",
                helper);
            using WindowsProcessActionService service = new(
                new WindowsProcessActionNative(),
                _ => ValueTask.FromResult(
                    new WindowsProcessActionService.BrokerGuard(true, null)));

            ProcessActionResult result =
                await service.EndProcessAsync(target, TestCancellation);

            Assert.Equal(ProcessActionStatus.Success, result.Status);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestCancellation);
            }
        }
    }

    [Fact]
    public async Task RealDisposableHelperWithStaleStartTimeIsRefusedAndRemainsAlive()
    {
        string helper = HelperExecutable();
        using Process process = StartHelper(helper);
        try
        {
            ProcessActionTarget current = TargetForProcess(process, helper);
            ProcessActionTarget stale = current with
            {
                InstanceId = new(
                    current.InstanceId.ProcessId,
                    current.InstanceId.StartTimeUtc.AddSeconds(-1))
            };
            using WindowsProcessActionService service = new(
                new WindowsProcessActionNative(),
                _ => ValueTask.FromResult(
                    new WindowsProcessActionService.BrokerGuard(true, null)));

            ProcessActionResult result =
                await service.EndProcessAsync(stale, TestCancellation);

            Assert.Equal(ProcessActionStatus.StaleProcessIdentity, result.Status);
            Assert.False(process.HasExited);
        }
        finally
        {
            await StopHelperAsync(process);
        }
    }

    [Fact]
    public async Task RealDisposableTreeEndsRootAndChildrenButNotUnrelatedHelper()
    {
        string helper = HelperExecutable();
        using Process unrelated = StartHelper(helper);
        using Process root = StartHelper(helper, "--children", "2");
        string? childLine = await root.StandardOutput.ReadLineAsync(TestCancellation);
        int[] childIds = childLine?
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToArray() ?? [];
        Assert.Equal(2, childIds.Length);
        Process[] children = childIds.Select(Process.GetProcessById).ToArray();
        try
        {
            ProcessActionTarget target = TargetForProcess(root, helper);
            using WindowsProcessActionService service = new(
                new WindowsProcessActionNative(),
                _ => ValueTask.FromResult(
                    new WindowsProcessActionService.BrokerGuard(true, null)));

            ProcessActionResult result =
                await service.EndProcessTreeAsync(target, TestCancellation);

            Assert.Equal(ProcessActionStatus.Success, result.Status);
            Assert.True(root.HasExited);
            Assert.All(children, child => Assert.True(child.HasExited));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            foreach (Process child in children)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync(TestCancellation);
                }

                child.Dispose();
            }

            await StopHelperAsync(root);
            await StopHelperAsync(unrelated);
        }
    }

    private static WindowsProcessActionService Service(
        FakeNative native,
        int? brokerProcessId = null) =>
        new(
            native,
            _ => ValueTask.FromResult(
                new WindowsProcessActionService.BrokerGuard(
                    true,
                    brokerProcessId)));

    private static FakeNative ReadyNative()
    {
        FakeNative native = new();
        native.Processes[120] = Info(120, 10, "sample");
        return native;
    }

    private static ProcessActionTarget Target(
        int processId,
        int startTicks,
        string name,
        string? path = null) =>
        new(
            new ProcessInstanceId(
                processId,
                DateTimeOffset.UnixEpoch.AddTicks(startTicks)),
            name,
            name,
            path);

    private static NativeProcessInfo Info(
        int processId,
        int startTicks,
        string name,
        string? path = null,
        bool verified = true,
        bool critical = false,
        bool protectedProcess = false) =>
        new(
            new ProcessInstanceId(
                processId,
                DateTimeOffset.UnixEpoch.AddTicks(startTicks)),
            name,
            path,
            verified,
            critical,
            protectedProcess);

    private static string HelperExecutable()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "MonitoringXS.ProcessActionTestHelper",
            "bin",
            configuration,
            "net10.0-windows10.0.17763.0",
            "MonitoringXS.ProcessActionTestHelper.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Process action test helper was not built.", path);
    }

    private static Process StartHelper(string executable, params string[] arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("Process action test helper did not start.");
    }

    private static ProcessActionTarget TargetForProcess(Process process, string executable) =>
        new(
            new ProcessInstanceId(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)),
            "Process action test helper",
            "MonitoringXS.ProcessActionTestHelper",
            executable);

    private static async Task StopHelperAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestCancellation);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "MonitoringXS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("MonitoringXS repository root was not found.");
    }

    private sealed class FakeNative : IProcessActionNative
    {
        public Dictionary<int, NativeProcessInfo> Processes { get; } = [];

        public IReadOnlyList<NativeProcessTreeEntry> Tree { get; set; } = [];

        public Func<IReadOnlyList<NativeProcessTreeEntry>>? TreeFactory { get; set; }

        public Func<ProcessActionTarget, ProcessActionStatus> Termination { get; set; } =
            _ => ProcessActionStatus.Success;

        public List<int> Terminated { get; } = [];

        public List<string> OpenedPaths { get; } = [];

        public NativeProcessRead Read(int processId) =>
            Processes.TryGetValue(processId, out NativeProcessInfo? process)
                ? new(ProcessActionStatus.Success, process)
                : new(ProcessActionStatus.AlreadyExited);

        public IReadOnlyList<NativeProcessTreeEntry> SnapshotTree() =>
            TreeFactory?.Invoke() ?? Tree;

        public ProcessActionStatus TerminateAndWait(
            ProcessActionTarget target,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Terminated.Add(target.InstanceId.ProcessId);
            return Termination(target);
        }

        public ProcessActionStatus OpenExplorerSelecting(string executablePath)
        {
            OpenedPaths.Add(executablePath);
            return ProcessActionStatus.Success;
        }
    }
}
