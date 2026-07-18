namespace MonitoringXS.Core.Models;

public enum MetricAvailability
{
    Available,
    Partial,
    WarmingUp,
    AccessDenied,
    Unsupported,
    Error
}
