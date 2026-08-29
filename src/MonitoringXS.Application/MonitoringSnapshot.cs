using MonitoringXS.Core.Models;

namespace MonitoringXS.Application;

public sealed record ApplicationHistoryPoint(DateTimeOffset Timestamp, double? CpuPercent, long? WorkingSetBytes);

public sealed record MonitoringSnapshot(
    DateTimeOffset CapturedAt,
    ProcessDiscoverySnapshot Discovery,
    IReadOnlyList<ApplicationMetricSnapshot> Applications,
    IReadOnlyDictionary<string, IReadOnlyList<ApplicationHistoryPoint>> OneMinuteHistory,
    SystemOverviewSnapshot? SystemOverview = null,
    IReadOnlyList<SystemOverviewHistoryPoint>? SystemOverviewHistory = null);
