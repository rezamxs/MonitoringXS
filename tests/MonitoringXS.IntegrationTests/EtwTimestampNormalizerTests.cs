using MonitoringXS.Platform.Windows.Metrics;

namespace MonitoringXS.IntegrationTests;

public sealed class EtwTimestampNormalizerTests
{
    [Fact]
    public void LocalEtwTimestampIsConvertedToUtcWithoutUsingQpc()
    {
        DateTime local = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Local);

        DateTimeOffset result = EtwTimestampNormalizer.NormalizeToUtc(local);

        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(local.ToUniversalTime(), result.UtcDateTime);
    }

    [Fact]
    public void EventQueueAndThreadMapAreExplicitlyBounded()
    {
        Assert.Equal(16_384, EtwPhysicalDiskEventSource.EventQueueCapacity);
        Assert.Equal(32_768, EtwPhysicalDiskEventSource.ThreadMapCapacity);
        Assert.Equal(32_768, EtwPhysicalDiskEventSource.IrpMapCapacity);
        Assert.Equal(32, EtwPhysicalDiskEventSource.EtwBufferSizeMegabytes);
    }
}
