using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IMetricHistoryStore : IDisposable, IAsyncDisposable
{
    MetricHistoryStoreDiagnostics Diagnostics { get; }

    ValueTask<MetricHistoryWriteResult> EnqueueAsync(
        IReadOnlyList<ApplicationMetricSnapshot> snapshots,
        CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);

    ValueTask<MetricHistoryApplicationsResult> ListApplicationsAsync(
        CancellationToken cancellationToken);

    ValueTask<MetricHistoryQueryResult> QueryAsync(
        string logicalApplicationId,
        MetricHistoryMetric metric,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}
