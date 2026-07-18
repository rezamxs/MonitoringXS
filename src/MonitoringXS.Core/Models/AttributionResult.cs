namespace MonitoringXS.Core.Models;

public sealed record AttributionResult(
    ProcessDescriptor Process,
    ApplicationIdentity? Application,
    bool IsHidden,
    string Reason)
{
    public static AttributionResult Hidden(ProcessDescriptor process, string reason) =>
        new(process, null, true, reason);

    public static AttributionResult Attributed(ProcessDescriptor process, ApplicationIdentity application) =>
        new(process, application, false, application.ClassificationReason);
}
