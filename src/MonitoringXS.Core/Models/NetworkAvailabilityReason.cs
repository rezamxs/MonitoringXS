namespace MonitoringXS.Core.Models;

public enum NetworkAvailabilityReason
{
    None,
    AccessDenied,
    Unsupported,
    SessionConflict,
    ResourceExhausted,
    CollectorError,
    EventLoss
}
