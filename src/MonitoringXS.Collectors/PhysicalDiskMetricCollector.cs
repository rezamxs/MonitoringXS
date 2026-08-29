using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class PhysicalDiskMetricCollector : IPhysicalDiskMetricCollector
{
    private static readonly TimeSpan MinimumRateInterval = TimeSpan.FromMilliseconds(10);
    private readonly IPhysicalDiskEventSource _eventSource;
    private readonly TimeProvider _timeProvider;
    // State is bounded by the currently attributed process set and evicted on every capture.
    private readonly Dictionary<ProcessInstanceId, ProcessState> _states = [];
    private long _lastEtwEventsLost;
    private long _lastQueueEventsDropped;
    private long _lastEventsObserved;
    private long _pidReuseEventsRejected;
    private long _unmatchedProcessEvents;
    private long _lastEventRateTimestamp;
    private bool _hasEventRateBaseline;
    private bool _sessionTotalsAreLowerBounds;

    public PhysicalDiskMetricCollector(IPhysicalDiskEventSource eventSource, TimeProvider? timeProvider = null)
    {
        _eventSource = eventSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyList<PhysicalDiskProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAtUtc = capturedAt.ToUniversalTime();
        long processingStarted = _timeProvider.GetTimestamp();
        ProcessInstanceId[] processInstances = processes.Select(item => item.InstanceId).ToArray();
        PhysicalDiskEventBatch batch = await _eventSource
            .ReadBatchAsync(processInstances, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        long captureTimestamp = _timeProvider.GetTimestamp();

        HashSet<ProcessInstanceId> live = processes.Select(item => item.InstanceId).ToHashSet();
        foreach (ProcessInstanceId stale in _states.Keys.Where(item => !live.Contains(item)).ToArray())
        {
            _states.Remove(stale);
        }

        double eventRate = CalculateEventRate(batch.EventsObserved, captureTimestamp);
        PhysicalDiskCollectorDiagnostics diagnostics = new(
            batch.EtwEventsLost,
            batch.QueueEventsDropped,
            batch.UnattributedEvents,
            _pidReuseEventsRejected,
            batch.EventsObserved,
            eventRate,
            batch.CurrentQueueDepth,
            batch.MaximumQueueDepth,
            batch.EtwBufferSizeMegabytes,
            batch.ReadEventsObserved,
            batch.WriteEventsObserved,
            batch.ReadBytesObserved,
            batch.WriteBytesObserved,
            batch.MetadataLookupFailures,
            batch.SessionStartFailures,
            batch.AccessDeniedFailures,
            ProcessingLatencyMilliseconds(processingStarted),
            batch.LastSuccessfulEventTimestampUtc,
            batch.Availability);
        if (batch.Availability is not (MetricAvailability.Available or MetricAvailability.Partial))
        {
            _states.Clear();
            _sessionTotalsAreLowerBounds = false;
            _hasEventRateBaseline = false;
            _lastEtwEventsLost = Math.Max(0, batch.EtwEventsLost);
            _lastQueueEventsDropped = Math.Max(0, batch.QueueEventsDropped);
            return processes
                .Select(process => Unavailable(process.InstanceId, capturedAtUtc, batch.Availability, batch.Detail, diagnostics))
                .ToArray();
        }

        Dictionary<int, ProcessDescriptor[]> candidatesByPid = processes
            .GroupBy(item => item.InstanceId.ProcessId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.InstanceId.StartTimeUtc).ToArray());
        Dictionary<ProcessInstanceId, IntervalCounts> intervalCounts = [];
        foreach (PhysicalDiskIoEvent diskEvent in batch.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidatesByPid.TryGetValue(diskEvent.ProcessId, out ProcessDescriptor[]? candidates))
            {
                _unmatchedProcessEvents = SaturatingIncrement(_unmatchedProcessEvents);
                continue;
            }

            // Both timestamps are UTC; newest-start-first prevents an old PID lifetime from receiving this event.
            ProcessDescriptor? matching = candidates.FirstOrDefault(
                candidate => diskEvent.TimestampUtc >= candidate.InstanceId.StartTimeUtc);
            if (matching is null)
            {
                _pidReuseEventsRejected = SaturatingIncrement(_pidReuseEventsRejected);
                continue;
            }

            intervalCounts.TryGetValue(matching.InstanceId, out IntervalCounts counts);
            intervalCounts[matching.InstanceId] = counts.Add(diskEvent);
        }

        diagnostics = diagnostics with
        {
            PidReuseEventsRejected = _pidReuseEventsRejected,
            UnattributedEvents = SaturatingAdd(batch.UnattributedEvents, _unmatchedProcessEvents),
            ProcessingLatencyMilliseconds = ProcessingLatencyMilliseconds(processingStarted)
        };
        bool intervalIncomplete = batch.Availability == MetricAvailability.Partial
            || batch.EtwEventsLost > _lastEtwEventsLost
            || batch.QueueEventsDropped > _lastQueueEventsDropped;
        _sessionTotalsAreLowerBounds |= intervalIncomplete;
        _lastEtwEventsLost = Math.Max(_lastEtwEventsLost, batch.EtwEventsLost);
        _lastQueueEventsDropped = Math.Max(_lastQueueEventsDropped, batch.QueueEventsDropped);
        diagnostics = diagnostics with
        {
            CollectorStatus = intervalIncomplete || _sessionTotalsAreLowerBounds
                ? MetricAvailability.Partial
                : batch.Availability,
            SessionTotalsAreLowerBounds = _sessionTotalsAreLowerBounds
        };
        string partialDetail = BuildPartialDetail(batch, diagnostics);

        List<PhysicalDiskProcessSample> samples = new(processes.Count);
        foreach (ProcessDescriptor process in processes)
        {
            intervalCounts.TryGetValue(process.InstanceId, out IntervalCounts interval);
            samples.Add(CreateSample(
                process.InstanceId,
                capturedAtUtc,
                captureTimestamp,
                interval,
                intervalIncomplete,
                partialDetail,
                diagnostics));
        }

        return samples;
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

    private PhysicalDiskProcessSample CreateSample(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        long captureTimestamp,
        IntervalCounts interval,
        bool intervalIncomplete,
        string partialDetail,
        PhysicalDiskCollectorDiagnostics diagnostics)
    {
        _states.TryGetValue(process, out ProcessState previous);
        ulong sessionReadBytes = SaturatingAdd(previous.SessionReadBytes, interval.ReadBytes);
        ulong sessionWriteBytes = SaturatingAdd(previous.SessionWriteBytes, interval.WriteBytes);
        ulong sessionReadOperations = SaturatingAdd(previous.SessionReadOperations, interval.ReadOperations);
        ulong sessionWriteOperations = SaturatingAdd(previous.SessionWriteOperations, interval.WriteOperations);
        MetricValue<ulong> readBytes = CompleteOrPartial(sessionReadBytes, _sessionTotalsAreLowerBounds, partialDetail);
        MetricValue<ulong> writeBytes = CompleteOrPartial(sessionWriteBytes, _sessionTotalsAreLowerBounds, partialDetail);
        MetricValue<ulong> readOperations = CompleteOrPartial(sessionReadOperations, _sessionTotalsAreLowerBounds, partialDetail);
        MetricValue<ulong> writeOperations = CompleteOrPartial(sessionWriteOperations, _sessionTotalsAreLowerBounds, partialDetail);

        if (!previous.HasRateBaseline)
        {
            _states[process] = new ProcessState(
                true,
                captureTimestamp,
                0,
                0,
                sessionReadBytes,
                sessionWriteBytes,
                sessionReadOperations,
                sessionWriteOperations);
            MetricValue<double> warming = MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second monotonic capture is required for a physical-disk rate.");
            return new PhysicalDiskProcessSample(
                process,
                capturedAtUtc,
                warming,
                warming,
                readBytes,
                writeBytes,
                readOperations,
                writeOperations,
                diagnostics);
        }

        ulong pendingReadBytes = SaturatingAdd(previous.PendingReadBytes, interval.ReadBytes);
        ulong pendingWriteBytes = SaturatingAdd(previous.PendingWriteBytes, interval.WriteBytes);
        TimeSpan elapsed = _timeProvider.GetElapsedTime(previous.RateTimestamp, captureTimestamp);
        if (elapsed <= TimeSpan.Zero)
        {
            _states[process] = previous with
            {
                PendingReadBytes = pendingReadBytes,
                PendingWriteBytes = pendingWriteBytes,
                SessionReadBytes = sessionReadBytes,
                SessionWriteBytes = sessionWriteBytes,
                SessionReadOperations = sessionReadOperations,
                SessionWriteOperations = sessionWriteOperations
            };
            MetricValue<double> invalid = MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "The physical-disk monotonic sampling interval was not positive.");
            return new PhysicalDiskProcessSample(
                process,
                capturedAtUtc,
                invalid,
                invalid,
                readBytes,
                writeBytes,
                readOperations,
                writeOperations,
                diagnostics);
        }

        if (elapsed < MinimumRateInterval)
        {
            _states[process] = previous with
            {
                PendingReadBytes = pendingReadBytes,
                PendingWriteBytes = pendingWriteBytes,
                SessionReadBytes = sessionReadBytes,
                SessionWriteBytes = sessionWriteBytes,
                SessionReadOperations = sessionReadOperations,
                SessionWriteOperations = sessionWriteOperations
            };
            MetricValue<double> warming = MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                $"At least {MinimumRateInterval.TotalMilliseconds:0} ms of monotonic time is required for a physical-disk rate.");
            return new PhysicalDiskProcessSample(
                process,
                capturedAtUtc,
                warming,
                warming,
                readBytes,
                writeBytes,
                readOperations,
                writeOperations,
                diagnostics);
        }

        _states[process] = new ProcessState(
            true,
            captureTimestamp,
            0,
            0,
            sessionReadBytes,
            sessionWriteBytes,
            sessionReadOperations,
            sessionWriteOperations);
        double readRate = pendingReadBytes / elapsed.TotalSeconds;
        double writeRate = pendingWriteBytes / elapsed.TotalSeconds;
        return new PhysicalDiskProcessSample(
            process,
            capturedAtUtc,
            CompleteOrPartial(readRate, intervalIncomplete, partialDetail),
            CompleteOrPartial(writeRate, intervalIncomplete, partialDetail),
            readBytes,
            writeBytes,
            readOperations,
            writeOperations,
            diagnostics);
    }

    private double ProcessingLatencyMilliseconds(long processingStarted)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(processingStarted, _timeProvider.GetTimestamp());
        return elapsed <= TimeSpan.Zero ? 0 : elapsed.TotalMilliseconds;
    }

    private static MetricValue<T> CompleteOrPartial<T>(T value, bool partial, string detail)
        where T : struct => partial
        ? MetricValue<T>.Partial(value, detail)
        : MetricValue<T>.Available(value);

    private static PhysicalDiskProcessSample Unavailable(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        MetricAvailability availability,
        string? detail,
        PhysicalDiskCollectorDiagnostics diagnostics) => new(
            process,
            capturedAtUtc,
            MetricValue<double>.Unavailable(availability, detail),
            MetricValue<double>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail),
            diagnostics);

    private static string BuildPartialDetail(
        PhysicalDiskEventBatch batch,
        PhysicalDiskCollectorDiagnostics diagnostics) =>
        $"Physical-disk values are lower bounds (ETW lost: {batch.EtwEventsLost}; queue dropped: {batch.QueueEventsDropped}; unattributed: {diagnostics.UnattributedEvents}; PID-reuse rejected: {diagnostics.PidReuseEventsRejected}).";

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static long SaturatingIncrement(long value) => value == long.MaxValue ? value : value + 1;

    private static long SaturatingAdd(long left, long right) =>
        long.MaxValue - left < right ? long.MaxValue : left + right;

    private readonly record struct ProcessState(
        bool HasRateBaseline,
        long RateTimestamp,
        ulong PendingReadBytes,
        ulong PendingWriteBytes,
        ulong SessionReadBytes,
        ulong SessionWriteBytes,
        ulong SessionReadOperations,
        ulong SessionWriteOperations);

    private readonly record struct IntervalCounts(
        ulong ReadBytes,
        ulong WriteBytes,
        ulong ReadOperations,
        ulong WriteOperations)
    {
        public IntervalCounts Add(PhysicalDiskIoEvent diskEvent) => diskEvent.Operation == PhysicalDiskOperation.Read
            ? this with
            {
                ReadBytes = SaturatingAdd(ReadBytes, (ulong)diskEvent.TransferSize),
                ReadOperations = SaturatingAdd(ReadOperations, 1)
            }
            : this with
            {
                WriteBytes = SaturatingAdd(WriteBytes, (ulong)diskEvent.TransferSize),
                WriteOperations = SaturatingAdd(WriteOperations, 1)
            };
    }
}
