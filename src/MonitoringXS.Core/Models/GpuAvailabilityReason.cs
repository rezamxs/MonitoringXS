namespace MonitoringXS.Core.Models;

public enum GpuAvailabilityReason
{
    None,
    WarmingUp,
    AccessDenied,
    UnsupportedOperatingSystem,
    UnsupportedDriver,
    CounterSetUnavailable,
    CounterUnavailable,
    ProviderUnavailable,
    ProcessUnavailable,
    ProcessExited,
    PidReused,
    AmbiguousCounterLifetime,
    InvalidData,
    CounterReadFailure,
    Error
}
