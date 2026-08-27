using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IMetricHistoryStore : IDisposable, IAsyncDisposable
{
    MetricHistoryStoreDiagnostics Diagnostics { get; }

    ValueTask<MetricHistoryWriteResult> EnqueueAsync(
        IReadOnlyList<ApplicationMetricSnapshot> snapshots,
        CancellationToken cancellationToken);

    ValueTask<MetricHistoryWriteResult> EnqueueAsync(
        MetricHistoryCapture capture,
        CancellationToken cancellationToken) =>
        EnqueueAsync(capture.Applications, cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);

    ValueTask<MetricHistoryApplicationsResult> ListApplicationsAsync(
        CancellationToken cancellationToken);

    ValueTask<MetricHistoryQueryResult> QueryAsync(
        string logicalApplicationId,
        MetricHistoryMetric metric,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    async ValueTask<IReadOnlyDictionary<MetricHistoryMetric, MetricHistoryQueryResult>> QueryManyAsync(
        string logicalApplicationId,
        IReadOnlyList<MetricHistoryMetric> metrics,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        Dictionary<MetricHistoryMetric, MetricHistoryQueryResult> results = [];
        foreach (MetricHistoryMetric metric in metrics.Distinct())
        {
            results[metric] = await QueryAsync(
                logicalApplicationId,
                metric,
                fromUtc,
                toUtc,
                cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}

public interface IMetricHistoryDiagnostics
{
    MetricHistoryStoreDiagnostics Diagnostics { get; }

    string DatabasePath { get; }

    int QueueCapacity { get; }

    TimeSpan Retention { get; }
}
