namespace MonitoringXS.Core.Models;

public enum MetricAvailability
{
    Available,
    Partial,
    WarmingUp,
    Unavailable,
    AccessDenied,
    Unsupported,
    Error
}
