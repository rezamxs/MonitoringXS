using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class ProcessMetricCollectorTests
{
    [Fact]
    public async Task SecondSampleCalculatesRealCounterDeltasAsRates()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        ProcessDescriptor descriptor = new(
            new ProcessInstanceId(42, start),
            "test",
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            true);
        SequenceResourceReader reader = new(
            Resource(TimeSpan.FromSeconds(1), 1000, new ProcessIoCounters(1, 2, 0, 100, 200, 0)),
            Resource(TimeSpan.FromSeconds(2), 2000, new ProcessIoCounters(2, 3, 0, 1124, 2248, 0)));
        ProcessMetricCollector collector = new(reader);
        DateTimeOffset firstCapture = DateTimeOffset.UtcNow;

        ProcessMetricSample first = Assert.Single(await collector.CollectAsync([descriptor], firstCapture, CancellationToken.None));
        ProcessMetricSample second = Assert.Single(await collector.CollectAsync([descriptor], firstCapture.AddSeconds(1), CancellationToken.None));

        Assert.Equal(MetricAvailability.WarmingUp, first.IoReadBytesPerSecond.Availability);
        Assert.Equal(1024, second.IoReadBytesPerSecond.Value);
        Assert.Equal(2048, second.IoWriteBytesPerSecond.Value);
        Assert.Equal(1124UL, second.TotalIoReadBytes.Value);
        Assert.Equal(3UL, second.IoWriteOperationCount.Value);
        Assert.True(second.CpuPercent.IsAvailable);
        Assert.Equal(2000, second.WorkingSetBytes.Value);
    }

    private static ProcessResourceCounters Resource(TimeSpan cpu, long memory, ProcessIoCounters io) =>
        new(cpu, memory, MetricValue<ProcessIoCounters>.Available(io));

    private sealed class SequenceResourceReader(params ProcessResourceCounters[] counters) : IProcessResourceCounterReader
    {
        private readonly Queue<ProcessResourceCounters> _counters = new(counters);

        public MetricValue<ProcessResourceCounters> Read(ProcessInstanceId process) =>
            MetricValue<ProcessResourceCounters>.Available(_counters.Dequeue());
    }
}
