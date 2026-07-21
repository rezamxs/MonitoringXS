using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IApplicationAttributionService
{
    ValueTask<IReadOnlyList<AttributionResult>> AttributeAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        CancellationToken cancellationToken);
}
