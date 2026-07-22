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
}
