using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IApplicationAttributionService
{
    IReadOnlyList<AttributionResult> Attribute(IReadOnlyList<ProcessDescriptor> processes);
}
