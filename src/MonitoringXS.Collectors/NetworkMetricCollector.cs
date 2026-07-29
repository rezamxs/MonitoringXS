using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class NetworkMetricCollector : INetworkMetricCollector
{
    private const int SystemProcessId = 4;
    private static readonly TimeSpan MinimumRateInterval = TimeSpan.FromMilliseconds(10);
    private readonly INetworkEventSource _eventSource;
    private readonly TimeProvider _timeProvider;
    // State is bounded by the currently attributed process set and evicted on every capture.
    private readonly Dictionary<ProcessInstanceId, ProcessState> _states = [];
    private long _lastEtwEventsLost;
    private long _lastQueueEventsDropped;
    private long _lastEventProcessingFailures;
    private long _lastUnsupportedEventVersions;
    private long _lastEventsObserved;
    private long _pidReuseEventsRejected;
    private long _attributedEvents;
    private long _systemProcessEventsRejected;
    private long _outsideApplicationSetEvents;
    private long _lastEventRateTimestamp;
    private long _processingSampleCount;
    private double _totalProcessingLatencyMilliseconds;
    private double _maximumProcessingLatencyMilliseconds;
    private bool _hasEventRateBaseline;
    private bool _sessionTotalsAreLowerBounds;
    private bool _hadUnavailableInterval;
    private bool _hadInterruptedBatch;

    public NetworkMetricCollector(INetworkEventSource eventSource, TimeProvider? timeProvider = null)
    {
        _eventSource = eventSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyList<NetworkProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAtUtc = capturedAt.ToUniversalTime();
        long processingStarted = _timeProvider.GetTimestamp();
        ProcessInstanceId[] processInstances = processes.Select(item => item.InstanceId).ToArray();
        NetworkEventBatch batch = await _eventSource
            .ReadNetworkBatchAsync(processInstances, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfCancellationRequestedAfterDrain(cancellationToken);
        long captureTimestamp = _timeProvider.GetTimestamp();

        HashSet<ProcessInstanceId> live = processes.Select(item => item.InstanceId).ToHashSet();
        foreach (ProcessInstanceId stale in _states.Keys.Where(item => !live.Contains(item)).ToArray())
        {
            _states.Remove(stale);
        }

        double eventRate = CalculateEventRate(batch.EventsObserved, captureTimestamp);
        if (batch.Availability is not (MetricAvailability.Available or MetricAvailability.Partial))
        {
            _states.Clear();
            _hadUnavailableInterval = true;
            _hasEventRateBaseline = false;
            _lastEtwEventsLost = Math.Max(0, batch.EtwEventsLost);
            _lastQueueEventsDropped = Math.Max(0, batch.QueueEventsDropped);
            _lastEventProcessingFailures = Math.Max(0, batch.EventProcessingFailures);
            _lastUnsupportedEventVersions = Math.Max(0, batch.UnsupportedEventVersions);
            RecordProcessingLatency(processingStarted);
            NetworkCollectorDiagnostics unavailableDiagnostics = Diagnostics(
                batch,
                eventRate,
                batch.Availability,
                batch.Detail,
                batch.Reason);
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
            ThrowIfCancellationRequestedAfterDrain(cancellationToken);
            if (networkEvent.ProcessId == SystemProcessId)
            {
                _systemProcessEventsRejected = SaturatingIncrement(_systemProcessEventsRejected);
                continue;
            }

            if (!candidatesByPid.TryGetValue(networkEvent.ProcessId, out ProcessDescriptor[]? candidates))
            {
                _outsideApplicationSetEvents = SaturatingIncrement(_outsideApplicationSetEvents);
                continue;
            }

            // ETW timestamps are normalized to UTC at the platform boundary before this lifetime check.
            ProcessDescriptor? matching = candidates.FirstOrDefault(
                candidate => networkEvent.TimestampUtc >= candidate.InstanceId.StartTimeUtc);
            if (matching is null)
            {
                _pidReuseEventsRejected = SaturatingIncrement(_pidReuseEventsRejected);
                continue;
            }

            _attributedEvents = SaturatingIncrement(_attributedEvents);
            intervalCounts.TryGetValue(matching.InstanceId, out IntervalCounts counts);
            intervalCounts[matching.InstanceId] = counts.Add(networkEvent);
        }

        bool etwLossIncreased = batch.EtwEventsLost > _lastEtwEventsLost;
        bool queueDropsIncreased = batch.QueueEventsDropped > _lastQueueEventsDropped;
        bool processingFailuresIncreased =
            batch.EventProcessingFailures > _lastEventProcessingFailures;
        bool unsupportedVersionsIncreased =
            batch.UnsupportedEventVersions > _lastUnsupportedEventVersions;
        bool recoveredAfterUnavailable = _hadUnavailableInterval;
        bool recoveredAfterInterruptedBatch = _hadInterruptedBatch;
        bool intervalIncomplete = batch.Availability == MetricAvailability.Partial
            || etwLossIncreased
            || queueDropsIncreased
            || processingFailuresIncreased
            || unsupportedVersionsIncreased
            || recoveredAfterUnavailable
            || recoveredAfterInterruptedBatch;
        NetworkAvailabilityReason diagnosticReason = ResolveReason(
            batch,
            etwLossIncreased,
            queueDropsIncreased,
            processingFailuresIncreased,
            unsupportedVersionsIncreased,
            recoveredAfterUnavailable || recoveredAfterInterruptedBatch);
        _sessionTotalsAreLowerBounds |= intervalIncomplete;
        _hadUnavailableInterval = false;
        _hadInterruptedBatch = false;
        _lastEtwEventsLost = Math.Max(_lastEtwEventsLost, batch.EtwEventsLost);
        _lastQueueEventsDropped = Math.Max(_lastQueueEventsDropped, batch.QueueEventsDropped);
        _lastEventProcessingFailures = Math.Max(
            _lastEventProcessingFailures,
            batch.EventProcessingFailures);
        _lastUnsupportedEventVersions = Math.Max(
            _lastUnsupportedEventVersions,
            batch.UnsupportedEventVersions);

        RecordProcessingLatency(processingStarted);
        MetricAvailability collectorStatus = intervalIncomplete || _sessionTotalsAreLowerBounds
            ? MetricAvailability.Partial
            : batch.CollectorStatus;
        string? statusReason = collectorStatus == MetricAvailability.Partial
            ? BuildPartialDetail(batch, recoveredAfterUnavailable, recoveredAfterInterruptedBatch)
            : batch.Detail;
        NetworkCollectorDiagnostics diagnostics = Diagnostics(
            batch,
            eventRate,
            collectorStatus,
            statusReason,
            diagnosticReason);
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
                captureTimestamp,
                interval,
                intervalIncomplete,
                partialDetail,
                tcpCount,
                udpCount,
                diagnostics));
        }

        return samples;
    }

    private NetworkCollectorDiagnostics Diagnostics(
        NetworkEventBatch batch,
        double eventRate,
        MetricAvailability collectorStatus,
        string? statusReason,
        NetworkAvailabilityReason reason) => new()
    {
        Reason = reason,
        EtwEventsLost = batch.EtwEventsLost,
        QueueEventsDropped = batch.QueueEventsDropped,
        UnattributedEvents = SaturatingAdd(
            batch.UnattributedEvents,
            SaturatingAdd(
                _systemProcessEventsRejected,
                SaturatingAdd(_outsideApplicationSetEvents, _pidReuseEventsRejected))),
        PidReuseEventsRejected = _pidReuseEventsRejected,
        EventsObserved = batch.EventsObserved,
        SendEvents = batch.SendEvents,
        ReceiveEvents = batch.ReceiveEvents,
        TcpSendEvents = batch.TcpSendEvents,
        TcpReceiveEvents = batch.TcpReceiveEvents,
        UdpSendEvents = batch.UdpSendEvents,
        UdpReceiveEvents = batch.UdpReceiveEvents,
        IPv4Events = batch.IPv4Events,
        IPv6Events = batch.IPv6Events,
        TotalSourceSendBytes = batch.TotalSourceSendBytes,
        TotalSourceReceiveBytes = batch.TotalSourceReceiveBytes,
        AttributedEvents = _attributedEvents,
        SystemProcessEvents = SaturatingAdd(
            batch.SystemProcessEvents,
            _systemProcessEventsRejected),
        OutsideApplicationSetEvents = _outsideApplicationSetEvents,
        UnknownProcessEvents = batch.UnknownProcessEvents,
        MetadataLookupFailures = batch.MetadataLookupFailures,
        SessionStartFailures = batch.SessionStartFailures,
        AccessDeniedFailures = batch.AccessDeniedFailures,
        EventProcessingFailures = batch.EventProcessingFailures,
        UnsupportedEventVersions = batch.UnsupportedEventVersions,
        EventRatePerSecond = eventRate,
        CurrentQueueDepth = batch.CurrentQueueDepth,
        MaximumQueueDepth = batch.MaximumQueueDepth,
        QueueCapacity = batch.QueueCapacity,
        EtwBufferSizeMegabytes = batch.EtwBufferSizeMegabytes,
        MaximumProcessingLatencyMilliseconds = _maximumProcessingLatencyMilliseconds,
        AverageProcessingLatencyMilliseconds = _processingSampleCount == 0
            ? 0
            : _totalProcessingLatencyMilliseconds / _processingSampleCount,
        LastSuccessfulEventTimestampUtc = batch.LastSuccessfulEventTimestampUtc,
        CollectorStatus = collectorStatus,
        CollectorStatusReason = statusReason,
        SessionTotalsAreLowerBounds = _sessionTotalsAreLowerBounds
    };

    private static NetworkAvailabilityReason ResolveReason(
        NetworkEventBatch batch,
        bool etwLossIncreased,
        bool queueDropsIncreased,
        bool processingFailuresIncreased,
        bool unsupportedVersionsIncreased,
        bool recoveredAfterUnavailable)
    {
        if (batch.Reason != NetworkAvailabilityReason.None)
        {
            return batch.Reason;
        }

        if (etwLossIncreased)
        {
            return NetworkAvailabilityReason.EventLoss;
        }

        if (queueDropsIncreased)
        {
            return NetworkAvailabilityReason.ResourceExhausted;
        }

        return processingFailuresIncreased
            || unsupportedVersionsIncreased
            || recoveredAfterUnavailable
                ? NetworkAvailabilityReason.CollectorError
                : NetworkAvailabilityReason.None;
    }

    private double CalculateEventRate(long eventsObserved, long captureTimestamp)
    {
        long observed = Math.Max(0, eventsObserved);
        if (!_hasEventRateBaseline)
        {
            _lastEventsObserved = observed;
            _lastEventRateTimestamp = captureTimestamp;
            _hasEventRateBaseline = true;
            return 0;
        }

        TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastEventRateTimestamp, captureTimestamp);
        if (elapsed < MinimumRateInterval)
        {
            return 0;
        }

        long eventDelta = Math.Max(0, observed - _lastEventsObserved);
        _lastEventsObserved = Math.Max(_lastEventsObserved, observed);
        _lastEventRateTimestamp = captureTimestamp;
        return eventDelta / elapsed.TotalSeconds;
    }

    private NetworkProcessSample CreateSample(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        long captureTimestamp,
        IntervalCounts interval,
        bool intervalIncomplete,
        string partialDetail,
        MetricValue<int> tcpCount,
        MetricValue<int> udpCount,
        NetworkCollectorDiagnostics diagnostics)
    {
        _states.TryGetValue(process, out ProcessState previous);
        ulong sessionDownloadedBytes = SaturatingAdd(
            previous.SessionDownloadedBytes,
            interval.DownloadedBytes);
        ulong sessionUploadedBytes = SaturatingAdd(
            previous.SessionUploadedBytes,
            interval.UploadedBytes);
        MetricValue<ulong> downloaded = CompleteOrPartial(
            sessionDownloadedBytes,
            _sessionTotalsAreLowerBounds,
            partialDetail);
        MetricValue<ulong> uploaded = CompleteOrPartial(
            sessionUploadedBytes,
            _sessionTotalsAreLowerBounds,
            partialDetail);

        if (!previous.HasRateBaseline)
        {
            _states[process] = new ProcessState(
                true,
                captureTimestamp,
                0,
                0,
                sessionDownloadedBytes,
                sessionUploadedBytes);
            MetricValue<double> warming = MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second monotonic capture is required for a network rate.");
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

        ulong pendingDownloadedBytes = SaturatingAdd(
            previous.PendingDownloadedBytes,
            interval.DownloadedBytes);
        ulong pendingUploadedBytes = SaturatingAdd(
            previous.PendingUploadedBytes,
            interval.UploadedBytes);
        TimeSpan elapsed = _timeProvider.GetElapsedTime(previous.RateTimestamp, captureTimestamp);
        if (elapsed <= TimeSpan.Zero)
        {
            _states[process] = previous with
            {
                PendingDownloadedBytes = pendingDownloadedBytes,
                PendingUploadedBytes = pendingUploadedBytes,
                SessionDownloadedBytes = sessionDownloadedBytes,
                SessionUploadedBytes = sessionUploadedBytes
            };
            MetricValue<double> invalid = MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "The network monotonic sampling interval was not positive.");
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

        if (elapsed < MinimumRateInterval)
        {
            _states[process] = previous with
            {
                PendingDownloadedBytes = pendingDownloadedBytes,
                PendingUploadedBytes = pendingUploadedBytes,
                SessionDownloadedBytes = sessionDownloadedBytes,
                SessionUploadedBytes = sessionUploadedBytes
            };
            MetricValue<double> warming = MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                $"At least {MinimumRateInterval.TotalMilliseconds:0} ms of monotonic time is required for a network rate.");
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

        _states[process] = new ProcessState(
            true,
            captureTimestamp,
            0,
            0,
            sessionDownloadedBytes,
            sessionUploadedBytes);
        return new NetworkProcessSample(
            process,
            capturedAtUtc,
            CompleteOrPartial(
                pendingDownloadedBytes / elapsed.TotalSeconds,
                intervalIncomplete,
                partialDetail),
            CompleteOrPartial(
                pendingUploadedBytes / elapsed.TotalSeconds,
                intervalIncomplete,
                partialDetail),
            downloaded,
            uploaded,
            tcpCount,
            udpCount,
            diagnostics);
    }

    private void RecordProcessingLatency(long processingStarted)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(processingStarted, _timeProvider.GetTimestamp());
        double milliseconds = elapsed <= TimeSpan.Zero ? 0 : elapsed.TotalMilliseconds;
        _processingSampleCount = SaturatingIncrement(_processingSampleCount);
        _totalProcessingLatencyMilliseconds = Math.Min(
            double.MaxValue,
            _totalProcessingLatencyMilliseconds + milliseconds);
        _maximumProcessingLatencyMilliseconds = Math.Max(
            _maximumProcessingLatencyMilliseconds,
            milliseconds);
    }

    private void ThrowIfCancellationRequestedAfterDrain(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The event source has already drained its bounded channel. If this batch is
        // abandoned, future totals must remain lower bounds rather than silently complete.
        _sessionTotalsAreLowerBounds = true;
        _hadInterruptedBatch = true;
        cancellationToken.ThrowIfCancellationRequested();
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

    private static string BuildPartialDetail(
        NetworkEventBatch batch,
        bool recoveredAfterUnavailable,
        bool recoveredAfterInterruptedBatch) =>
        $"Network values are lower bounds (ETW lost: {batch.EtwEventsLost}; queue dropped: {batch.QueueEventsDropped}; processing failures: {batch.EventProcessingFailures}; unsupported versions: {batch.UnsupportedEventVersions}; collector restart: {recoveredAfterUnavailable}; interrupted batch: {recoveredAfterInterruptedBatch}).";

    private static string BuildPartialDetail(NetworkCollectorDiagnostics diagnostics) =>
        !string.IsNullOrWhiteSpace(diagnostics.CollectorStatusReason)
            ? diagnostics.CollectorStatusReason
            : $"Network values are lower bounds (ETW lost: {diagnostics.EtwEventsLost}; queue dropped: {diagnostics.QueueEventsDropped}; unattributed: {diagnostics.UnattributedEvents}; PID-reuse rejected: {diagnostics.PidReuseEventsRejected}).";

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static long SaturatingIncrement(long value) => value == long.MaxValue ? value : value + 1;

    private static long SaturatingAdd(long left, long right) =>
        long.MaxValue - left < right ? long.MaxValue : left + right;

    private readonly record struct ProcessState(
        bool HasRateBaseline,
        long RateTimestamp,
        ulong PendingDownloadedBytes,
        ulong PendingUploadedBytes,
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
