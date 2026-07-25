using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class NetworkMetricAggregationService : INetworkMetricAggregationService
{
    private const int MaximumRetainedApplications = 512;
    // Exited helper totals remain with a still-running logical application, but the
    // state is evicted as soon as that entire application leaves the active snapshot.
    private readonly Dictionary<string, ApplicationSessionState> _sessionStates = new(StringComparer.Ordinal);
    // A process may be reclassified without restarting. Keep its last source total
    // independently so historical bytes are not transferred to the new application.
    // Entries are bounded by the current attributed process snapshot.
    private readonly Dictionary<ProcessInstanceId, ProcessBaseline> _processBaselines = [];

    public IReadOnlyDictionary<string, NetworkMetricSet> Aggregate(
        IReadOnlyList<AttributionResult> attribution,
        IReadOnlyList<NetworkProcessSample> metrics)
    {
        Dictionary<ProcessInstanceId, NetworkProcessSample> byProcess = metrics.ToDictionary(item => item.Process);
        IGrouping<string, AttributionResult>[] groups = attribution
            .Where(item => !item.IsHidden && item.Application is not null)
            .GroupBy(item => item.Application!.LogicalApplicationId, StringComparer.Ordinal)
            .ToArray();
        HashSet<string> activeApplicationIds = groups
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<ProcessInstanceId> activeProcesses = groups
            .SelectMany(group => group.Select(item => item.Process.InstanceId))
            .ToHashSet();
        foreach (string staleApplicationId in _sessionStates.Keys
                     .Where(id => !activeApplicationIds.Contains(id))
                     .ToArray())
        {
            _sessionStates.Remove(staleApplicationId);
        }

        foreach (ProcessInstanceId staleProcess in _processBaselines.Keys
                     .Where(process => !activeProcesses.Contains(process))
                     .ToArray())
        {
            _processBaselines.Remove(staleProcess);
        }

        return groups.ToDictionary(
                group => group.Key,
                group => AggregateApplication(
                    group.Key,
                    group.Select(item => item.Process.InstanceId).ToArray(),
                    byProcess),
                StringComparer.Ordinal);
    }

    private NetworkMetricSet AggregateApplication(
        string logicalApplicationId,
        ProcessInstanceId[] processes,
        IReadOnlyDictionary<ProcessInstanceId, NetworkProcessSample> metrics)
    {
        NetworkProcessSample[] samples = processes
            .Select(process => metrics.GetValueOrDefault(process))
            .Where(sample => sample is not null)
            .Cast<NetworkProcessSample>()
            .ToArray();
        // Collector diagnostics are session-wide and identical on every process sample.
        NetworkCollectorDiagnostics diagnostics = samples
            .Select(item => item.Diagnostics)
            .FirstOrDefault();
        (MetricValue<ulong> downloaded, MetricValue<ulong> uploaded, bool lowerBound) =
            RetainApplicationTotals(logicalApplicationId, processes, samples);
        diagnostics = diagnostics with
        {
            SessionTotalsAreLowerBounds = diagnostics.SessionTotalsAreLowerBounds || lowerBound
        };

        return new NetworkMetricSet(
            SumDouble(samples.Select(item => item.DownloadBytesPerSecond), processes.Length),
            SumDouble(samples.Select(item => item.UploadBytesPerSecond), processes.Length),
            downloaded,
            uploaded,
            SumInt(samples.Select(item => item.ActiveTcpConnectionCount), processes.Length),
            SumInt(samples.Select(item => item.UdpEndpointCount), processes.Length),
            diagnostics);
    }

    private (MetricValue<ulong> Downloaded, MetricValue<ulong> Uploaded, bool LowerBound)
        RetainApplicationTotals(
            string logicalApplicationId,
            ProcessInstanceId[] processes,
            NetworkProcessSample[] samples)
    {
        if (processes.Length == 0)
        {
            return (
                MetricValue<ulong>.Unavailable(MetricAvailability.WarmingUp),
                MetricValue<ulong>.Unavailable(MetricAvailability.WarmingUp),
                false);
        }

        if (!_sessionStates.TryGetValue(logicalApplicationId, out ApplicationSessionState? state))
        {
            if (_sessionStates.Count >= MaximumRetainedApplications)
            {
                string retentionLimitDetail =
                    $"Network total retention is capped at {MaximumRetainedApplications} active logical applications.";
                RecordUnretainedBaselines(samples);
                return (
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable, retentionLimitDetail),
                    MetricValue<ulong>.Unavailable(MetricAvailability.Unavailable, retentionLimitDetail),
                    true);
            }

            state = new ApplicationSessionState();
            _sessionStates.Add(logicalApplicationId, state);
        }

        bool lowerBound = state.LowerBound || samples.Length != processes.Length;
        HashSet<ProcessInstanceId> activeProcesses = processes.ToHashSet();
        foreach (NetworkProcessSample sample in samples)
        {
            if (!sample.SessionDownloadedBytes.IsAvailable || !sample.SessionUploadedBytes.IsAvailable)
            {
                lowerBound = true;
                continue;
            }

            ulong currentDownloaded = sample.SessionDownloadedBytes.Value!.Value;
            ulong currentUploaded = sample.SessionUploadedBytes.Value!.Value;
            bool hasApplicationBaseline = state.ProcessTotals.TryGetValue(
                sample.Process,
                out ProcessTotals previous);
            bool resumedAfterRetentionLimit = false;
            if (!hasApplicationBaseline
                && _processBaselines.TryGetValue(sample.Process, out ProcessBaseline processBaseline))
            {
                previous = processBaseline.Totals;
                resumedAfterRetentionLimit = processBaseline.WasUnretained;
            }

            ulong downloadedDelta = currentDownloaded >= previous.Downloaded
                ? currentDownloaded - previous.Downloaded
                : currentDownloaded;
            ulong uploadedDelta = currentUploaded >= previous.Uploaded
                ? currentUploaded - previous.Uploaded
                : currentUploaded;
            if (currentDownloaded < previous.Downloaded || currentUploaded < previous.Uploaded)
            {
                lowerBound = true;
            }

            lowerBound |= resumedAfterRetentionLimit;
            state.Downloaded = SaturatingAdd(state.Downloaded, downloadedDelta);
            state.Uploaded = SaturatingAdd(state.Uploaded, uploadedDelta);
            state.ProcessTotals[sample.Process] = new ProcessTotals(currentDownloaded, currentUploaded);
            _processBaselines[sample.Process] = new ProcessBaseline(
                new ProcessTotals(currentDownloaded, currentUploaded),
                false);
            lowerBound |= !sample.SessionDownloadedBytes.IsComplete
                || !sample.SessionUploadedBytes.IsComplete;
        }

        foreach (ProcessInstanceId exited in state.ProcessTotals.Keys
                     .Where(process => !activeProcesses.Contains(process))
                     .ToArray())
        {
            state.ProcessTotals.Remove(exited);
        }

        state.LowerBound = lowerBound;
        bool currentTotalsUnavailable = samples.Length > 0
            && samples.All(sample => !sample.SessionDownloadedBytes.IsAvailable
                || !sample.SessionUploadedBytes.IsAvailable);
        if (currentTotalsUnavailable)
        {
            return (
                SumUnsigned(samples.Select(item => item.SessionDownloadedBytes), processes.Length),
                SumUnsigned(samples.Select(item => item.SessionUploadedBytes), processes.Length),
                true);
        }

        string detail = "The logical-application session total is a lower bound.";
        return (
            lowerBound
                ? MetricValue<ulong>.Partial(state.Downloaded, detail)
                : MetricValue<ulong>.Available(state.Downloaded),
            lowerBound
                ? MetricValue<ulong>.Partial(state.Uploaded, detail)
                : MetricValue<ulong>.Available(state.Uploaded),
            lowerBound);
    }

    private static MetricValue<double> SumDouble(IEnumerable<MetricValue<double>> values, int expectedCount)
    {
        MetricValue<double>[] items = values.ToArray();
        double[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length == 0)
        {
            return Unavailable<double>(items);
        }

        double sum = available.Sum();
        return available.Length == expectedCount && items.All(item => item.IsComplete)
            ? MetricValue<double>.Available(sum)
            : MetricValue<double>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
    }

    private static MetricValue<ulong> SumUnsigned(IEnumerable<MetricValue<ulong>> values, int expectedCount)
    {
        MetricValue<ulong>[] items = values.ToArray();
        ulong[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length == 0)
        {
            return Unavailable<ulong>(items);
        }

        ulong sum = available.Aggregate(0UL, SaturatingAdd);
        return available.Length == expectedCount && items.All(item => item.IsComplete)
            ? MetricValue<ulong>.Available(sum)
            : MetricValue<ulong>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
    }

    private static MetricValue<int> SumInt(IEnumerable<MetricValue<int>> values, int expectedCount)
    {
        MetricValue<int>[] items = values.ToArray();
        int[] available = items.Where(item => item.IsAvailable).Select(item => item.Value!.Value).ToArray();
        if (available.Length == 0)
        {
            return Unavailable<int>(items);
        }

        int sum = available.Aggregate(0, SaturatingAdd);
        return available.Length == expectedCount && items.All(item => item.IsComplete)
            ? MetricValue<int>.Available(sum)
            : MetricValue<int>.Partial(sum, PartialDetail(items.Count(item => item.IsComplete), expectedCount));
    }

    private void RecordUnretainedBaselines(IEnumerable<NetworkProcessSample> samples)
    {
        foreach (NetworkProcessSample sample in samples)
        {
            if (!sample.SessionDownloadedBytes.IsAvailable || !sample.SessionUploadedBytes.IsAvailable)
            {
                continue;
            }

            _processBaselines[sample.Process] = new ProcessBaseline(
                new ProcessTotals(
                    sample.SessionDownloadedBytes.Value!.Value,
                    sample.SessionUploadedBytes.Value!.Value),
                true);
        }
    }

    private static MetricValue<T> Unavailable<T>(IReadOnlyList<MetricValue<T>> values)
        where T : struct
    {
        foreach (MetricValue<T> item in values)
        {
            if (item.Availability != MetricAvailability.Available)
            {
                return MetricValue<T>.Unavailable(item.Availability, item.Detail);
            }
        }

        return MetricValue<T>.Unavailable(MetricAvailability.WarmingUp);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static int SaturatingAdd(int left, int right) =>
        int.MaxValue - left < right ? int.MaxValue : left + right;

    private static string PartialDetail(int complete, int total) =>
        $"Only {complete} of {total} network process samples were completely available; the displayed value is a lower bound.";

    private sealed class ApplicationSessionState
    {
        public Dictionary<ProcessInstanceId, ProcessTotals> ProcessTotals { get; } = [];

        public ulong Downloaded { get; set; }

        public ulong Uploaded { get; set; }

        public bool LowerBound { get; set; }
    }

    private readonly record struct ProcessTotals(ulong Downloaded, ulong Uploaded);

    private readonly record struct ProcessBaseline(ProcessTotals Totals, bool WasUnretained);
}
