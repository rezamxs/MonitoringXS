using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

/// <summary>
/// Provides system-wide resource metrics (CPU, memory, disk, network, GPU).
/// Implementations must not fabricate data; unavailable metrics must be reported
/// with the appropriate <see cref="MetricAvailability"/> state.
/// </summary>
public interface ISystemOverviewProvider
{
    /// <summary>
    /// Captures a system-wide snapshot. The first call may return WarmingUp for
    /// delta-based metrics (CPU, disk rates, network rates).
    /// </summary>
    ValueTask<SystemOverviewSnapshot> CaptureAsync(CancellationToken cancellationToken);
}