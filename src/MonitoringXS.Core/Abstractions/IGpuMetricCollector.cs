using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IGpuMetricCollector
{
    ValueTask<IReadOnlyList<GpuProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken);
}
