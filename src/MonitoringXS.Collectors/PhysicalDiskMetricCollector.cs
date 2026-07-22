using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class PhysicalDiskMetricCollector : IPhysicalDiskMetricCollector
{
    private readonly IPhysicalDiskEventSource _eventSource;
    // State is bounded by the currently attributed process set and evicted on every capture.
    private readonly Dictionary<ProcessInstanceId, ProcessState> _states = [];
    private long _lastEtwEventsLost;
    private long _lastQueueEventsDropped;
    private long _lastUnattributedEvents;
    private long _lastEventsObserved;
    private long _pidReuseEventsRejected;
    private DateTimeOffset _lastCaptureAtUtc;

    public PhysicalDiskMetricCollector(IPhysicalDiskEventSource eventSource)
    {
        _eventSource = eventSource;
    }

    public async ValueTask<IReadOnlyList<PhysicalDiskProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAtUtc = capturedAt.ToUniversalTime();
        PhysicalDiskEventBatch batch = await _eventSource.ReadBatchAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        HashSet<ProcessInstanceId> live = processes.Select(item => item.InstanceId).ToHashSet();
        foreach (ProcessInstanceId stale in _states.Keys.Where(item => !live.Contains(item)).ToArray())
        {
            _states.Remove(stale);
        }

        double eventRate = CalculateEventRate(batch.EventsObserved, capturedAtUtc);
        PhysicalDiskCollectorDiagnostics diagnostics = new(
            batch.EtwEventsLost,
            batch.QueueEventsDropped,
            batch.UnattributedEvents,
            _pidReuseEventsRejected,
            batch.EventsObserved,
            eventRate,
            batch.CurrentQueueDepth,
            batch.MaximumQueueDepth,
            batch.EtwBufferSizeMegabytes);
        if (batch.Availability is not (MetricAvailability.Available or MetricAvailability.Partial))
        {
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

        diagnostics = diagnostics with { PidReuseEventsRejected = _pidReuseEventsRejected };
        bool degraded = batch.Availability == MetricAvailability.Partial
            || batch.EtwEventsLost > _lastEtwEventsLost
            || batch.QueueEventsDropped > _lastQueueEventsDropped;
        _lastEtwEventsLost = Math.Max(_lastEtwEventsLost, batch.EtwEventsLost);
        _lastQueueEventsDropped = Math.Max(_lastQueueEventsDropped, batch.QueueEventsDropped);
        _lastUnattributedEvents = Math.Max(_lastUnattributedEvents, batch.UnattributedEvents);
        string partialDetail = BuildPartialDetail(batch, diagnostics);

        List<PhysicalDiskProcessSample> samples = new(processes.Count);
        foreach (ProcessDescriptor process in processes)
        {
            intervalCounts.TryGetValue(process.InstanceId, out IntervalCounts interval);
            samples.Add(CreateSample(process.InstanceId, capturedAtUtc, interval, degraded, partialDetail, diagnostics));
        }

        return samples;
    }

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

    private PhysicalDiskProcessSample CreateSample(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        IntervalCounts interval,
        bool degraded,
        string partialDetail,
        PhysicalDiskCollectorDiagnostics diagnostics)
    {
        _states.TryGetValue(process, out ProcessState previous);
        ProcessState current = new(
            capturedAtUtc,
            SaturatingAdd(previous.SessionReadBytes, interval.ReadBytes),
            SaturatingAdd(previous.SessionWriteBytes, interval.WriteBytes),
            SaturatingAdd(previous.SessionReadOperations, interval.ReadOperations),
            SaturatingAdd(previous.SessionWriteOperations, interval.WriteOperations));
        _states[process] = current;

        MetricValue<ulong> readBytes = CompleteOrPartial(current.SessionReadBytes, degraded, partialDetail);
        MetricValue<ulong> writeBytes = CompleteOrPartial(current.SessionWriteBytes, degraded, partialDetail);
        MetricValue<ulong> readOperations = CompleteOrPartial(current.SessionReadOperations, degraded, partialDetail);
        MetricValue<ulong> writeOperations = CompleteOrPartial(current.SessionWriteOperations, degraded, partialDetail);

        if (previous.CapturedAtUtc == default)
        {
            MetricValue<double> warming = MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second UTC capture is required for a physical-disk rate.");
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

        double elapsedSeconds = (capturedAtUtc - previous.CapturedAtUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            MetricValue<double> invalid = MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "The physical-disk sampling interval was not positive after UTC normalization.");
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

        double readRate = interval.ReadBytes / elapsedSeconds;
        double writeRate = interval.WriteBytes / elapsedSeconds;
        return new PhysicalDiskProcessSample(
            process,
            capturedAtUtc,
            CompleteOrPartial(readRate, degraded, partialDetail),
            CompleteOrPartial(writeRate, degraded, partialDetail),
            readBytes,
            writeBytes,
            readOperations,
            writeOperations,
            diagnostics);
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
        $"Physical-disk values are lower bounds (ETW lost: {batch.EtwEventsLost}; queue dropped: {batch.QueueEventsDropped}; unattributed: {batch.UnattributedEvents}; PID-reuse rejected: {diagnostics.PidReuseEventsRejected}).";

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static long SaturatingIncrement(long value) => value == long.MaxValue ? value : value + 1;

    private readonly record struct ProcessState(
        DateTimeOffset CapturedAtUtc,
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
