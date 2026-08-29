using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IGpuMetricCollector
{
    GpuCounterBatch? LastBatch => null;

    ValueTask<IReadOnlyList<GpuProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken);
}
