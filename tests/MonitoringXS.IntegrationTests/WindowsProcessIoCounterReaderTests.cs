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

    [Fact]
    public void CurrentProcessReturnsCombinedResourceCountersFromOneHandle()
    {
        using Process current = Process.GetCurrentProcess();
        ProcessInstanceId instance = new(
            current.Id,
            new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero));

        MetricValue<ProcessResourceCounters> result = new WindowsProcessResourceCounterReader().Read(instance);

        Assert.True(result.IsComplete, result.Detail);
        Assert.True(result.Value.HasValue);
        Assert.True(result.Value.Value.WorkingSetBytes > 0);
        Assert.True(result.Value.Value.TotalProcessorTime >= TimeSpan.Zero);
        Assert.True(result.Value.Value.IoCounters.IsComplete, result.Value.Value.IoCounters.Detail);
    }
}
