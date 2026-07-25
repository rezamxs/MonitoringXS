using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Tests;

public sealed class PhysicalDiskTimeDomainTests
{
    [Fact]
    public void ProcessInstanceIdNormalizesStartTimeToUtc()
    {
        DateTimeOffset localDomain = new(2026, 7, 21, 12, 0, 0, TimeSpan.FromHours(3.5));

        ProcessInstanceId instance = new(42, localDomain);

        Assert.Equal(TimeSpan.Zero, instance.StartTimeUtc.Offset);
        Assert.Equal(localDomain.UtcDateTime, instance.StartTimeUtc.UtcDateTime);
    }

    [Fact]
    public void PhysicalDiskEventNormalizesTimestampToUtc()
    {
        DateTimeOffset localDomain = new(2026, 7, 21, 12, 0, 1, TimeSpan.FromHours(3.5));

        PhysicalDiskIoEvent diskEvent = new(42, 7, localDomain, PhysicalDiskOperation.Read, 4096);

        Assert.Equal(TimeSpan.Zero, diskEvent.TimestampUtc.Offset);
        Assert.Equal(localDomain.UtcDateTime, diskEvent.TimestampUtc.UtcDateTime);
    }

    [Fact]
    public void NetworkEventNormalizesTimestampToSameUtcDomainAsProcessIdentity()
    {
        DateTimeOffset localDomain = new(2026, 7, 21, 12, 0, 1, TimeSpan.FromHours(3.5));

        ProcessInstanceId process = new(42, localDomain.AddSeconds(-1));
        NetworkTrafficEvent networkEvent = new(
            42,
            localDomain,
            NetworkDirection.Download,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv4,
            4096);

        Assert.Equal(TimeSpan.Zero, process.StartTimeUtc.Offset);
        Assert.Equal(TimeSpan.Zero, networkEvent.TimestampUtc.Offset);
        Assert.True(networkEvent.TimestampUtc >= process.StartTimeUtc);
        Assert.Equal(NetworkAddressFamily.IPv4, networkEvent.AddressFamily);
    }

    [Fact]
    public void NetworkEventRejectsInvalidTransferSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkTrafficEvent(
            42,
            DateTimeOffset.UtcNow,
            NetworkDirection.Upload,
            NetworkTransport.Udp,
            NetworkAddressFamily.IPv6,
            -1));
    }
}
