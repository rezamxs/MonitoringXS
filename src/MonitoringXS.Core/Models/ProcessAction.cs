namespace MonitoringXS.Core.Models;

public enum ProcessActionStatus
{
    Success,
    AlreadyExited,
    StaleProcessIdentity,
    AccessDenied,
    ProtectedProcess,
    InvalidTarget,
    ExecutablePathUnavailable,
    OperationTimedOut,
    Cancelled,
    PartialTreeTermination,
    Failed
}

public sealed record ProcessActionTarget(
    ProcessInstanceId InstanceId,
    string DisplayName,
    string ProcessName,
    string? ExpectedExecutablePath);

public sealed record ProcessActionInspection(
    ProcessActionStatus Status,
    string Message,
    string? ExecutablePath = null,
    int DescendantCount = 0)
{
    public bool IsValid => Status == ProcessActionStatus.Success;
}

public sealed record ProcessActionResult(
    ProcessActionStatus Status,
    string Message,
    int TerminatedCount = 0,
    int FailedCount = 0)
{
    public bool IsSuccess => Status == ProcessActionStatus.Success;
}
