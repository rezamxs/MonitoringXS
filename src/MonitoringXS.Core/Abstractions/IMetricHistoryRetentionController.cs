using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IMetricHistoryRetentionController
{
    ValueTask<MetricHistoryRetentionResult> UpdateRetentionAsync(
        TimeSpan retention,
        CancellationToken cancellationToken);
}
