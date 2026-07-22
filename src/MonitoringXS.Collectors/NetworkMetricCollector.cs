using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class NetworkMetricCollector : INetworkMetricCollector
{
    private readonly INetworkEventSource _eventSource;
    // State is bounded by the currently attributed process set and evicted on every capture.
    private readonly Dictionary<ProcessInstanceId, ProcessState> _states = [];
    private long _lastEtwEventsLost;
    private long _lastQueueEventsDropped;
    private long _lastEventsObserved;
    private long _pidReuseEventsRejected;
    private DateTimeOffset _lastCaptureAtUtc;
    private bool _sessionTotalsAreLowerBounds;

    public NetworkMetricCollector(INetworkEventSource eventSource)
    {
        _eventSource = eventSource;
    }

    public async ValueTask<IReadOnlyList<NetworkProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAtUtc = capturedAt.ToUniversalTime();
        NetworkEventBatch batch = await _eventSource.ReadNetworkBatchAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        HashSet<ProcessInstanceId> live = processes.Select(item => item.InstanceId).ToHashSet();
        foreach (ProcessInstanceId stale in _states.Keys.Where(item => !live.Contains(item)).ToArray())
        {
            _states.Remove(stale);
        }

        double eventRate = CalculateEventRate(batch.EventsObserved, capturedAtUtc);
        if (batch.Availability is not (MetricAvailability.Available or MetricAvailability.Partial))
        {
            _states.Clear();
            _sessionTotalsAreLowerBounds = false;
            NetworkCollectorDiagnostics unavailableDiagnostics = Diagnostics(batch, eventRate);
            return processes
                .Select(process => Unavailable(
                    process.InstanceId,
                    capturedAtUtc,
                    batch.Availability,
                    batch.Detail,
                    unavailableDiagnostics))
                .ToArray();
        }

        Dictionary<int, ProcessDescriptor[]> candidatesByPid = processes
            .GroupBy(item => item.InstanceId.ProcessId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.InstanceId.StartTimeUtc).ToArray());
        Dictionary<ProcessInstanceId, IntervalCounts> intervalCounts = [];
        foreach (NetworkTrafficEvent networkEvent in batch.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidatesByPid.TryGetValue(networkEvent.ProcessId, out ProcessDescriptor[]? candidates))
            {
                continue;
            }

            ProcessDescriptor? matching = candidates.FirstOrDefault(
                candidate => networkEvent.TimestampUtc >= candidate.InstanceId.StartTimeUtc);
            if (matching is null)
            {
                _pidReuseEventsRejected = SaturatingIncrement(_pidReuseEventsRejected);
                continue;
            }

            intervalCounts.TryGetValue(matching.InstanceId, out IntervalCounts counts);
            intervalCounts[matching.InstanceId] = counts.Add(networkEvent);
        }

        bool intervalIncomplete = batch.Availability == MetricAvailability.Partial
            || batch.EtwEventsLost > _lastEtwEventsLost
            || batch.QueueEventsDropped > _lastQueueEventsDropped;
        _sessionTotalsAreLowerBounds |= intervalIncomplete;
        _lastEtwEventsLost = Math.Max(_lastEtwEventsLost, batch.EtwEventsLost);
        _lastQueueEventsDropped = Math.Max(_lastQueueEventsDropped, batch.QueueEventsDropped);
        NetworkCollectorDiagnostics diagnostics = Diagnostics(batch, eventRate) with
        {
            PidReuseEventsRejected = _pidReuseEventsRejected,
            SessionTotalsAreLowerBounds = _sessionTotalsAreLowerBounds
        };
        string partialDetail = BuildPartialDetail(diagnostics);

        List<NetworkProcessSample> samples = new(processes.Count);
        foreach (ProcessDescriptor process in processes)
        {
            intervalCounts.TryGetValue(process.InstanceId, out IntervalCounts interval);
            MetricValue<int> tcpCount = EndpointCount(batch.ActiveTcpConnectionsByProcess, process.InstanceId.ProcessId);
            MetricValue<int> udpCount = EndpointCount(batch.UdpEndpointsByProcess, process.InstanceId.ProcessId);
            samples.Add(CreateSample(
                process.InstanceId,
                capturedAtUtc,
                interval,
                intervalIncomplete,
                partialDetail,
                tcpCount,
                udpCount,
                diagnostics));
        }

        return samples;
    }

    private NetworkCollectorDiagnostics Diagnostics(NetworkEventBatch batch, double eventRate) => new(
        batch.Reason,
        batch.EtwEventsLost,
        batch.QueueEventsDropped,
        batch.UnattributedEvents,
        _pidReuseEventsRejected,
        batch.EventsObserved,
        eventRate,
        batch.CurrentQueueDepth,
        batch.MaximumQueueDepth,
        batch.EtwBufferSizeMegabytes,
        _sessionTotalsAreLowerBounds);

    private double CalculateEventRate(long eventsObserved, DateTimeOffset capturedAtUtc)
    {
        if (_lastCaptureAtUtc == default)
        {
            _lastEventsObserved = Math.Max(0, eventsObserved);
            _lastCaptureAtUtc = capturedAtUtc;
            return 0;
        }

        double elapsedSeconds = (capturedAtUtc - _lastCaptureAtUtc).TotalSeconds;
        long eventDelta = Math.Max(0, eventsObserved - _lastEventsObserved);
        _lastEventsObserved = Math.Max(_lastEventsObserved, eventsObserved);
        _lastCaptureAtUtc = capturedAtUtc;
        return elapsedSeconds > 0 ? eventDelta / elapsedSeconds : 0;
    }

    private NetworkProcessSample CreateSample(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        IntervalCounts interval,
        bool intervalIncomplete,
        string partialDetail,
        MetricValue<int> tcpCount,
        MetricValue<int> udpCount,
        NetworkCollectorDiagnostics diagnostics)
    {
        _states.TryGetValue(process, out ProcessState previous);
        ProcessState current = new(
            capturedAtUtc,
            SaturatingAdd(previous.SessionDownloadedBytes, interval.DownloadedBytes),
            SaturatingAdd(previous.SessionUploadedBytes, interval.UploadedBytes));
        _states[process] = current;

        MetricValue<ulong> downloaded = CompleteOrPartial(
            current.SessionDownloadedBytes,
            _sessionTotalsAreLowerBounds,
            partialDetail);
        MetricValue<ulong> uploaded = CompleteOrPartial(
            current.SessionUploadedBytes,
            _sessionTotalsAreLowerBounds,
            partialDetail);

        if (previous.CapturedAtUtc == default)
        {
            MetricValue<double> warming = MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second UTC capture is required for a network rate.");
            return new NetworkProcessSample(
                process,
                capturedAtUtc,
                warming,
                warming,
                downloaded,
                uploaded,
                tcpCount,
                udpCount,
                diagnostics);
        }

        double elapsedSeconds = (capturedAtUtc - previous.CapturedAtUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            MetricValue<double> invalid = MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "The network sampling interval was not positive after UTC normalization.");
            return new NetworkProcessSample(
                process,
                capturedAtUtc,
                invalid,
                invalid,
                downloaded,
                uploaded,
                tcpCount,
                udpCount,
                diagnostics);
        }

        return new NetworkProcessSample(
            process,
            capturedAtUtc,
            CompleteOrPartial(interval.DownloadedBytes / elapsedSeconds, intervalIncomplete, partialDetail),
            CompleteOrPartial(interval.UploadedBytes / elapsedSeconds, intervalIncomplete, partialDetail),
            downloaded,
            uploaded,
            tcpCount,
            udpCount,
            diagnostics);
    }

    private static MetricValue<int> EndpointCount(IReadOnlyDictionary<int, int>? counts, int processId) =>
        counts is null
            ? MetricValue<int>.Unavailable(
                MetricAvailability.Unsupported,
                "This collector does not provide a reliable endpoint snapshot.")
            : MetricValue<int>.Available(Math.Max(0, counts.GetValueOrDefault(processId)));

    private static MetricValue<T> CompleteOrPartial<T>(T value, bool partial, string detail)
        where T : struct => partial
        ? MetricValue<T>.Partial(value, detail)
        : MetricValue<T>.Available(value);

    private static NetworkProcessSample Unavailable(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        MetricAvailability availability,
        string? detail,
        NetworkCollectorDiagnostics diagnostics) => new(
        process,
        capturedAtUtc,
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<int>.Unavailable(availability, detail),
        MetricValue<int>.Unavailable(availability, detail),
        diagnostics);

    private static string BuildPartialDetail(NetworkCollectorDiagnostics diagnostics) =>
        $"Network values are lower bounds (ETW lost: {diagnostics.EtwEventsLost}; queue dropped: {diagnostics.QueueEventsDropped}; PID-reuse rejected: {diagnostics.PidReuseEventsRejected}).";

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static long SaturatingIncrement(long value) => value == long.MaxValue ? value : value + 1;

    private readonly record struct ProcessState(
        DateTimeOffset CapturedAtUtc,
        ulong SessionDownloadedBytes,
        ulong SessionUploadedBytes);

    private readonly record struct IntervalCounts(ulong DownloadedBytes, ulong UploadedBytes)
    {
        public IntervalCounts Add(NetworkTrafficEvent networkEvent) =>
            networkEvent.Direction == NetworkDirection.Download
                ? this with { DownloadedBytes = SaturatingAdd(DownloadedBytes, (ulong)networkEvent.TransferSize) }
                : this with { UploadedBytes = SaturatingAdd(UploadedBytes, (ulong)networkEvent.TransferSize) };
    }
}
