using System.Diagnostics;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class ProcessMetricCollectorTests
{
    [Fact]
    public async Task SecondSampleCalculatesRealCounterDeltasAsRates()
    {
        using Process current = Process.GetCurrentProcess();
        DateTimeOffset start = new(current.StartTime.ToUniversalTime(), TimeSpan.Zero);
        ProcessDescriptor descriptor = new(
            new ProcessInstanceId(current.Id, start),
            current.ProcessName,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            true);
        SequenceIoReader reader = new(
            new ProcessIoCounters(1, 2, 0, 100, 200, 0),
            new ProcessIoCounters(2, 3, 0, 1124, 2248, 0));
        ProcessMetricCollector collector = new(reader);
        DateTimeOffset firstCapture = DateTimeOffset.UtcNow;

        ProcessMetricSample first = Assert.Single(await collector.CollectAsync([descriptor], firstCapture, CancellationToken.None));
        ProcessMetricSample second = Assert.Single(await collector.CollectAsync([descriptor], firstCapture.AddSeconds(1), CancellationToken.None));

        Assert.Equal(MetricAvailability.WarmingUp, first.IoReadBytesPerSecond.Availability);
        Assert.Equal(1024, second.IoReadBytesPerSecond.Value);
        Assert.Equal(2048, second.IoWriteBytesPerSecond.Value);
        Assert.Equal(1124UL, second.TotalIoReadBytes.Value);
        Assert.Equal(3UL, second.IoWriteOperationCount.Value);
    }

    private sealed class SequenceIoReader(params ProcessIoCounters[] counters) : IProcessIoCounterReader
    {
        private readonly Queue<ProcessIoCounters> _counters = new(counters);

        public MetricValue<ProcessIoCounters> Read(ProcessInstanceId process) =>
            MetricValue<ProcessIoCounters>.Available(_counters.Dequeue());
    }
}
