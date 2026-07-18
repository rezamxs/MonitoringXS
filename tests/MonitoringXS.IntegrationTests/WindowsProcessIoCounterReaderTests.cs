using System.Diagnostics;
using MonitoringXS.Platform.Windows.Metrics;
using MonitoringXS.Core.Models;

namespace MonitoringXS.IntegrationTests;

public sealed class WindowsProcessIoCounterReaderTests
{
    [Fact]
    public void CurrentProcessReturnsRealCumulativeIoCounters()
    {
        using Process current = Process.GetCurrentProcess();
        ProcessInstanceId instance = new(
            current.Id,
            new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero));

        MetricValue<ProcessIoCounters> result = new WindowsProcessIoCounterReader().Read(instance);

        Assert.True(result.IsComplete, result.Detail);
        Assert.True(result.Value.HasValue);
    }
}
