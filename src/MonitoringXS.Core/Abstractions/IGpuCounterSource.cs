using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IGpuCounterSource
{
    ValueTask<GpuCounterBatch> CaptureAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken);
}
