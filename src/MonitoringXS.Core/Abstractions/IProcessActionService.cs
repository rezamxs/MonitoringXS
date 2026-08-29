using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IProcessActionService
{
    ValueTask<ProcessActionInspection> InspectAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken);

    ValueTask<ProcessActionInspection> InspectTreeAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken);

    ValueTask<ProcessActionResult> EndProcessAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken);

    ValueTask<ProcessActionResult> EndProcessTreeAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken);

    ValueTask<ProcessActionResult> OpenFileLocationAsync(
        ProcessActionTarget target,
        CancellationToken cancellationToken);
}
