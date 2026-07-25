using MonitoringXS.Core.Models;
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
    public void NetworkStatisticsKeepProtocolAndAddressFamilyCountersSeparate()
    {
        NetworkEventStatistics statistics = new();

        Assert.True(statistics.TryRecord(
            NetworkDirection.Upload,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv4,
            100));
        Assert.True(statistics.TryRecord(
            NetworkDirection.Download,
            NetworkTransport.Udp,
            NetworkAddressFamily.IPv6,
            200));

        NetworkEventStatistics.Snapshot snapshot = statistics.Read();
        Assert.Equal(2, snapshot.EventsObserved);
        Assert.Equal(1, snapshot.TcpSendEvents);
        Assert.Equal(1, snapshot.UdpReceiveEvents);
        Assert.Equal(1, snapshot.IPv4Events);
        Assert.Equal(1, snapshot.IPv6Events);
        Assert.Equal(100UL, snapshot.SourceSendBytes);
        Assert.Equal(200UL, snapshot.SourceReceiveBytes);
    }

    [Fact]
    public void NetworkStatisticsAcceptZeroBytesAndRejectNegativeSizes()
    {
        NetworkEventStatistics statistics = new();

        Assert.True(statistics.TryRecord(
            NetworkDirection.Download,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv4,
            0));
        Assert.False(statistics.TryRecord(
            NetworkDirection.Upload,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv4,
            -1));

        NetworkEventStatistics.Snapshot snapshot = statistics.Read();
        Assert.Equal(2, snapshot.EventsObserved);
        Assert.Equal(1, snapshot.ReceiveEvents);
        Assert.Equal(0UL, snapshot.SourceReceiveBytes);
        Assert.Equal(1, snapshot.EventProcessingFailures);
        Assert.Equal(1, snapshot.UnattributedEvents);
    }

    [Fact]
    public void NetworkStatisticsDoNotMixSystemAndUnknownProcesses()
    {
        NetworkEventStatistics statistics = new();

        statistics.RecordSystemProcess();
        statistics.RecordUnknownProcess();
        statistics.RecordUnsupportedEventVersion();

        NetworkEventStatistics.Snapshot snapshot = statistics.Read();
        Assert.Equal(1, snapshot.SystemProcessEvents);
        Assert.Equal(1, snapshot.UnknownProcessEvents);
        Assert.Equal(1, snapshot.UnsupportedEventVersions);
        Assert.Equal(3, snapshot.UnattributedEvents);
    }

    [Fact]
    public void MalformedTransferSizeAccessorIsContainedAndCounted()
    {
        Assert.False(EtwPhysicalDiskEventSource.TryReadNetworkTransferSize(
            new object(),
            static _ => throw new InvalidOperationException("Malformed typed payload."),
            out int transferSize));
        Assert.Equal(0, transferSize);

        NetworkEventStatistics statistics = new();
        statistics.RecordMalformedEvent();
        NetworkEventStatistics.Snapshot snapshot = statistics.Read();

        Assert.Equal(1, snapshot.EventsObserved);
        Assert.Equal(1, snapshot.EventProcessingFailures);
        Assert.Equal(1, snapshot.UnattributedEvents);
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
