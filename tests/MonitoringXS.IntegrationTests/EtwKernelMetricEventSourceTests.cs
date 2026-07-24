using MonitoringXS.Platform.Windows.Metrics;

namespace MonitoringXS.IntegrationTests;

public sealed class EtwKernelMetricEventSourceTests
{
    [Fact]
    public void SharedKernelSessionUsesNeutralVersionedName()
    {
        Assert.Equal("MonitoringXS.KernelMetrics.v1", EtwPhysicalDiskEventSource.SessionName);
        Assert.DoesNotContain("PhysicalDisk", EtwPhysicalDiskEventSource.SessionName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Network", EtwPhysicalDiskEventSource.SessionName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposalBeforeInitializationIsIdempotent()
    {
        EtwPhysicalDiskEventSource source = new();

        source.Dispose();
        source.Dispose();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task ReadAfterDisposalDoesNotStartABackgroundSession()
    {
        EtwPhysicalDiskEventSource source = new();
        await source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await source.ReadBatchAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IrpCorrelationSurvivesMultipleSplitCompletions()
    {
        EtwPhysicalDiskEventSource.BoundedIrpProcessMap map = new(4);
        const ulong irp = 0xffff9e13c4412010;

        map.Set(irp, 4242);

        Assert.True(map.TryGetValue(irp, out int firstCompletion));
        Assert.True(map.TryGetValue(irp, out int secondCompletion));
        Assert.Equal(4242, firstCompletion);
        Assert.Equal(4242, secondCompletion);
    }

    [Fact]
    public void IrpCorrelationIsReplacedOnPointerReuseAndRemainsBounded()
    {
        EtwPhysicalDiskEventSource.BoundedIrpProcessMap map = new(2);

        map.Set(1, 100);
        map.Set(1, 200);
        Assert.True(map.TryGetValue(1, out int reusedProcess));
        Assert.Equal(200, reusedProcess);

        map.Set(2, 300);
        map.Set(3, 400);

        Assert.False(map.TryGetValue(1, out _));
        Assert.True(map.TryGetValue(2, out int retainedProcess));
        Assert.Equal(300, retainedProcess);
        Assert.True(map.TryGetValue(3, out int newestProcess));
        Assert.Equal(400, newestProcess);
    }

    [Fact]
    public void IrpCorrelationPreservesUserInitiatorAcrossSystemSplitInit()
    {
        EtwPhysicalDiskEventSource.BoundedIrpProcessMap map = new(2);

        map.Set(10, 4242);
        map.Set(10, 4);

        Assert.True(map.TryGetValue(10, out int processId));
        Assert.Equal(4242, processId);
    }
}
