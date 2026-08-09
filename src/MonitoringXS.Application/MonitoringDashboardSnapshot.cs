using MonitoringXS.Core.Models;

namespace MonitoringXS.Application;

public sealed record ApplicationHistoryPoint(DateTimeOffset Timestamp, double? CpuPercent, long? WorkingSetBytes);

public sealed record MonitoringDashboardSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ApplicationMetricSnapshot> InstalledApplications,
    IReadOnlyList<ApplicationMetricSnapshot> PortableApplications,
    IReadOnlyDictionary<string, IReadOnlyList<ApplicationHistoryPoint>> OneMinuteHistory,
    SystemOverviewSnapshot? SystemOverview = null,
    IReadOnlyList<SystemOverviewHistoryPoint>? SystemOverviewHistory = null);
