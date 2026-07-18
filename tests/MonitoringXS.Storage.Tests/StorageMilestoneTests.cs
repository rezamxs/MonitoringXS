using MonitoringXS.Storage;

namespace MonitoringXS.Storage.Tests;

public sealed class StorageMilestoneTests
{
    [Fact]
    public void StatusDoesNotClaimUnimplementedPersistence() =>
        Assert.Contains("not yet claimed", StorageMilestone.Status, StringComparison.OrdinalIgnoreCase);
}
