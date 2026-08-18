using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Processes;

namespace MonitoringXS.IntegrationTests;

public sealed class WindowsProcessDiscoveryServiceTests
{
    [Fact]
    public async Task SuccessfulEnumerationKeepsPidAndStartTimeIdentity()
    {
        DateTimeOffset started = new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);
        WindowsProcessDiscoveryService service = Service(
            [new NativeProcessTree.ProcessEntry(42, 1, "example.exe")],
            (_, _) => NativeProcessDetails.ProcessDetailsReadResult.Success(
                new(started, @"C:\Apps\example.exe", false)),
            AvailableMetadata(@"C:\Apps\example.exe"));

        ProcessDiscoverySnapshot result = await service.DiscoverAsync(
            TestContext.Current.CancellationToken);

        ProcessDescriptor process = Assert.Single(result.Processes);
        Assert.Equal([42], result.ObservedProcessIds);
        Assert.Equal(new ProcessInstanceId(42, started), process.InstanceId);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task StaticDetailsCacheUsesPidAndStartTimeIdentity()
    {
        DateTimeOffset firstStart = DateTimeOffset.UtcNow.AddMinutes(-2);
        DateTimeOffset reusedStart = DateTimeOffset.UtcNow.AddMinutes(-1);
        int calls = 0;
        WindowsProcessDiscoveryService service = Service(
            [new NativeProcessTree.ProcessEntry(42, 1, "example.exe")],
            (_, cached) =>
            {
                calls++;
                DateTimeOffset start = calls == 3 ? reusedStart : firstStart;
                if (calls == 2)
                {
                    Assert.NotNull(cached);
                    Assert.Equal(firstStart, cached!.StartTimeUtc);
                }

                if (calls == 3)
                {
                    Assert.NotNull(cached);
                    Assert.Equal(firstStart, cached!.StartTimeUtc);
                }

                return NativeProcessDetails.ProcessDetailsReadResult.Success(new(start, @"C:\Apps\example.exe", false));
            },
            AvailableMetadata(@"C:\Apps\example.exe"));

        await service.DiscoverAsync(TestContext.Current.CancellationToken);
        await service.DiscoverAsync(TestContext.Current.CancellationToken);
        ProcessDiscoverySnapshot reused = await service.DiscoverAsync(TestContext.Current.CancellationToken);
        await service.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(reusedStart, Assert.Single(reused.Processes).InstanceId.StartTimeUtc);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task ParentNameRequiresCurrentParentLifetimeToPrecedeChild()
    {
        DateTimeOffset parentStart = DateTimeOffset.UtcNow.AddMinutes(-3);
        DateTimeOffset childStart = DateTimeOffset.UtcNow.AddMinutes(-2);
        WindowsProcessDiscoveryService service = Service(
            [
                new NativeProcessTree.ProcessEntry(10, 1, "parent.exe"),
                new NativeProcessTree.ProcessEntry(11, 10, "child.exe")
            ],
            (pid, _) => NativeProcessDetails.ProcessDetailsReadResult.Success(new(
                pid == 10 ? parentStart : childStart,
                null,
                false)),
            AvailableMetadata(@"C:\unused.exe"));

        ProcessDiscoverySnapshot result = await service.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal("parent", result.Processes.Single(process => process.InstanceId.ProcessId == 11).ParentProcessName);
    }

    [Fact]
    public async Task ReusedParentPidDoesNotSupplyParentName()
    {
        DateTimeOffset childStart = DateTimeOffset.UtcNow.AddMinutes(-3);
        DateTimeOffset reusedParentStart = DateTimeOffset.UtcNow.AddMinutes(-1);
        WindowsProcessDiscoveryService service = Service(
            [
                new NativeProcessTree.ProcessEntry(10, 1, "other.exe"),
                new NativeProcessTree.ProcessEntry(11, 10, "child.exe")
            ],
            (pid, _) => NativeProcessDetails.ProcessDetailsReadResult.Success(new(
                pid == 10 ? reusedParentStart : childStart,
                null,
                false)),
            AvailableMetadata(@"C:\unused.exe"));

        ProcessDiscoverySnapshot result = await service.DiscoverAsync(TestContext.Current.CancellationToken);

        ProcessDescriptor child = result.Processes.Single(process => process.InstanceId.ProcessId == 11);
        Assert.Equal(10, child.ParentProcessId);
        Assert.Null(child.ParentProcessName);
    }

    [Fact]
    public async Task ProcessExitDuringMaterializationIsPartialNotFatal()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        WindowsProcessDiscoveryService service = Service(
            [
                new NativeProcessTree.ProcessEntry(42, 1, "live.exe"),
                new NativeProcessTree.ProcessEntry(43, 1, "exited.exe")
            ],
            (pid, _) => pid == 42
                ? NativeProcessDetails.ProcessDetailsReadResult.Success(
                    new(started, null, false))
                : NativeProcessDetails.ProcessDetailsReadResult.Failed(
                    NativeProcessDetails.ProcessDetailsReadFailure.ProcessExited),
            AvailableMetadata(@"C:\unused.exe"));

        ProcessDiscoverySnapshot result = await service.DiscoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal([42, 43], result.ObservedProcessIds);
        Assert.Equal(42, Assert.Single(result.Processes).InstanceId.ProcessId);
        Assert.Contains(result.Issues, issue =>
            issue.ProcessId == 43 && issue.Kind == ProcessDiscoveryIssueKind.ProcessExited);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public async Task AccessDeniedMetadataKeepsDescriptorAndSuccessfulEnumeration()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        string path = @"C:\Protected\example.exe";
        ExecutableMetadata unavailable = new(
            path,
            null,
            null,
            null,
            null,
            0,
            DateTimeOffset.MinValue,
            false,
            nameof(UnauthorizedAccessException));
        WindowsProcessDiscoveryService service = Service(
            [new NativeProcessTree.ProcessEntry(50, 1, "example.exe")],
            (_, _) => NativeProcessDetails.ProcessDetailsReadResult.Success(
                new(started, path, false)),
            unavailable,
            hasVisibleWindow: true);

        ProcessDiscoverySnapshot result = await service.DiscoverAsync(
            TestContext.Current.CancellationToken);

        ProcessDescriptor process = Assert.Single(result.Processes);
        Assert.Equal(new ProcessInstanceId(50, started), process.InstanceId);
        Assert.Equal(path, process.ExecutablePath);
        Assert.Contains(result.Issues, issue =>
            issue.ProcessId == 50 && issue.Kind == ProcessDiscoveryIssueKind.AccessDenied);
    }

    [Fact]
    public async Task AccessDeniedExecutablePathDoesNotDiscardProcessIdentity()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        WindowsProcessDiscoveryService service = Service(
            [new NativeProcessTree.ProcessEntry(60, 1, "example.exe")],
            (_, _) => NativeProcessDetails.ProcessDetailsReadResult.Success(
                new(
                    started,
                    null,
                    false,
                    NativeProcessDetails.ProcessDetailsReadFailure.AccessDenied)),
            AvailableMetadata(@"C:\unused.exe"));

        ProcessDiscoverySnapshot result = await service.DiscoverAsync(
            TestContext.Current.CancellationToken);

        ProcessDescriptor process = Assert.Single(result.Processes);
        Assert.Null(process.ExecutablePath);
        Assert.Equal(new ProcessInstanceId(60, started), process.InstanceId);
        Assert.Contains(result.Issues, issue =>
            issue.ProcessId == 60 && issue.Kind == ProcessDiscoveryIssueKind.AccessDenied);
    }

    [Fact]
    public async Task CancellationAndFatalEnumerationRemainDistinct()
    {
        WindowsProcessDiscoveryService service = Service(
            [],
            (_, _) => throw new Xunit.Sdk.XunitException("Details must not run."),
            AvailableMetadata(@"C:\unused.exe"));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.DiscoverAsync(canceled.Token));

        WindowsProcessDiscoveryService fatal = new(
            new MetadataProvider(AvailableMetadata(@"C:\unused.exe")),
            () => throw new InvalidOperationException("enumeration failed"),
            () => new Dictionary<int, NativeWindowSnapshot.WindowDescriptor>(),
            (_, _) => throw new Xunit.Sdk.XunitException("Details must not run."));
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fatal.DiscoverAsync(TestContext.Current.CancellationToken));
        Assert.Equal("enumeration failed", exception.Message);
    }

    private static WindowsProcessDiscoveryService Service(
        IReadOnlyList<NativeProcessTree.ProcessEntry> processes,
        Func<int, NativeProcessDetails.ProcessDetails?, NativeProcessDetails.ProcessDetailsReadResult> details,
        ExecutableMetadata metadata,
        bool hasVisibleWindow = false)
    {
        Dictionary<int, NativeWindowSnapshot.WindowDescriptor> windows = hasVisibleWindow
            ? processes.ToDictionary(
                process => process.ProcessId,
                process => new NativeWindowSnapshot.WindowDescriptor((nint)process.ProcessId, process.ExecutableName))
            : [];
        return new(
            new MetadataProvider(metadata),
            () => processes,
            () => windows,
            details);
    }

    private static ExecutableMetadata AvailableMetadata(string path) => new(
        path,
        "Example",
        "Example",
        "Publisher",
        "1.0",
        1,
        DateTimeOffset.UtcNow,
        true,
        null);

    private sealed class MetadataProvider(ExecutableMetadata metadata) : IExecutableMetadataProvider
    {
        public ValueTask<ExecutableMetadata> GetMetadataAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(metadata);
        }
    }
}
