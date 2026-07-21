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
}
