using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Metrics;

public sealed class EtwPhysicalDiskEventSource : IPhysicalDiskEventSource, INetworkEventSource, IDisposable, IAsyncDisposable
{
    private const int SystemProcessId = 4;
    public const string SessionName = "MonitoringXS.KernelMetrics.v1";
    public const int EventQueueCapacity = 16_384;
    public const int NetworkEventQueueCapacity = 16_384;
    public const int ThreadMapCapacity = 32_768;
    public const int IrpMapCapacity = 32_768;
    public const int EtwBufferSizeMegabytes = 32;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private const uint ThreadQueryLimitedInformation = 0x0800;
    // Callbacks use TryWrite below: a full queue drops and counts the new event instead of blocking ETW processing.
    private readonly Channel<PhysicalDiskIoEvent> _events = Channel.CreateBounded<PhysicalDiskIoEvent>(
        new BoundedChannelOptions(EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    private readonly Channel<NetworkTrafficEvent> _networkEvents = Channel.CreateBounded<NetworkTrafficEvent>(
        new BoundedChannelOptions(NetworkEventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    private readonly BoundedThreadProcessMap _threadProcesses = new(ThreadMapCapacity);
    private readonly BoundedIrpProcessMap _irpProcesses = new(IrpMapCapacity);
    private readonly NetworkEventStatistics _networkStatistics = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private TaskCompletionSource<MetricAvailability>? _started;
    private Task? _runTask;
    private TraceEventSession? _session;
    private ETWTraceEventSource? _source;
    private DateTimeOffset _nextRetryUtc;
    private MetricAvailability _availability = MetricAvailability.WarmingUp;
    private string? _detail = "Starting the kernel metric ETW session.";
    private MetricAvailability _networkAvailability = MetricAvailability.WarmingUp;
    private string? _networkDetail = "Starting the kernel metric ETW session.";
    private long _queueEventsDropped;
    private long _unattributedEvents;
    private long _eventsObserved;
    private long _readEventsObserved;
    private long _writeEventsObserved;
    private long _readBytesObserved;
    private long _writeBytesObserved;
    private long _metadataLookupFailures;
    private long _sessionStartFailures;
    private long _accessDeniedFailures;
    private long _lastSuccessfulEventTimestampUtcTicks;
    private long _etwEventsLost;
    private long _lastReportedEtwEventsLost;
    private long _lastReportedNetworkEtwEventsLost;
    private long _networkQueueEventsDropped;
    private long _lastReportedNetworkQueueEventsDropped;
    private long _lastReportedNetworkProcessingFailures;
    private int _maximumQueueDepth;
    private int _networkMaximumQueueDepth;
    private NetworkAvailabilityReason _networkReason;
    private bool _disposed;

    public async ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<MetricAvailability> started = EnsureStarted();
        await started.WaitAsync(cancellationToken).ConfigureAwait(false);

        MetricAvailability availability;
        string? detail;
        long eventsLost;
        lock (_gate)
        {
            availability = _availability;
            detail = _detail;
            eventsLost = Interlocked.Read(ref _etwEventsLost);
            if (_session is not null)
            {
                try
                {
                    eventsLost = Math.Max(eventsLost, Math.Max(0, _session.EventsLost));
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        Interlocked.Exchange(ref _etwEventsLost, eventsLost);
        int depthBeforeDrain = _events.Reader.Count;
        List<PhysicalDiskIoEvent> events = CreateDrainBuffer<PhysicalDiskIoEvent>(depthBeforeDrain);
        while (_events.Reader.TryRead(out PhysicalDiskIoEvent? diskEvent))
        {
            events.Add(diskEvent);
        }

        if (eventsLost > Interlocked.Read(ref _lastReportedEtwEventsLost))
        {
            Interlocked.Exchange(ref _lastReportedEtwEventsLost, eventsLost);
            events.Clear();
            _threadProcesses.Clear();
            _irpProcesses.Clear();
            availability = MetricAvailability.Partial;
            detail = "ETW reported lost events; the current batch and thread mapping were discarded to prevent PID misattribution.";
        }

        return new PhysicalDiskEventBatch(
            events,
            availability,
            eventsLost,
            Interlocked.Read(ref _queueEventsDropped),
            Interlocked.Read(ref _unattributedEvents),
            detail,
            Interlocked.Read(ref _eventsObserved),
            depthBeforeDrain,
            Volatile.Read(ref _maximumQueueDepth),
            EtwBufferSizeMegabytes,
            Interlocked.Read(ref _readEventsObserved),
            Interlocked.Read(ref _writeEventsObserved),
            ToUnsigned(Interlocked.Read(ref _readBytesObserved)),
            ToUnsigned(Interlocked.Read(ref _writeBytesObserved)),
            Interlocked.Read(ref _metadataLookupFailures),
            Interlocked.Read(ref _sessionStartFailures),
            Interlocked.Read(ref _accessDeniedFailures),
            ReadLastSuccessfulEventTimestamp());
    }

    public async ValueTask<NetworkEventBatch> ReadNetworkBatchAsync(
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<MetricAvailability> started = EnsureStarted();
        await started.WaitAsync(cancellationToken).ConfigureAwait(false);

        MetricAvailability availability;
        NetworkAvailabilityReason reason;
        string? detail;
        long eventsLost;
        lock (_gate)
        {
            availability = _networkAvailability;
            reason = _networkReason;
            detail = _networkDetail;
            eventsLost = Interlocked.Read(ref _etwEventsLost);
            if (_session is not null)
            {
                try
                {
                    eventsLost = Math.Max(eventsLost, Math.Max(0, _session.EventsLost));
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        Interlocked.Exchange(ref _etwEventsLost, eventsLost);
        int depthBeforeDrain = _networkEvents.Reader.Count;
        List<NetworkTrafficEvent> events = CreateDrainBuffer<NetworkTrafficEvent>(depthBeforeDrain);
        while (_networkEvents.Reader.TryRead(out NetworkTrafficEvent? networkEvent))
        {
            events.Add(networkEvent);
        }

        long queueEventsDropped = Interlocked.Read(ref _networkQueueEventsDropped);
        NetworkEventStatistics.Snapshot statistics = _networkStatistics.Read();
        if (eventsLost > Interlocked.Read(ref _lastReportedNetworkEtwEventsLost))
        {
            Interlocked.Exchange(ref _lastReportedNetworkEtwEventsLost, eventsLost);
            events.Clear();
            _threadProcesses.Clear();
            _irpProcesses.Clear();
            availability = MetricAvailability.Partial;
            reason = NetworkAvailabilityReason.EventLoss;
            detail = "ETW reported lost events; the current network batch was discarded to prevent PID misattribution.";
        }
        else if (queueEventsDropped > Interlocked.Read(ref _lastReportedNetworkQueueEventsDropped))
        {
            Interlocked.Exchange(ref _lastReportedNetworkQueueEventsDropped, queueEventsDropped);
            availability = MetricAvailability.Partial;
            reason = NetworkAvailabilityReason.ResourceExhausted;
            detail = "The bounded network event queue overflowed; retained values are lower bounds.";
        }
        else if (statistics.EventProcessingFailures > Interlocked.Read(ref _lastReportedNetworkProcessingFailures))
        {
            Interlocked.Exchange(
                ref _lastReportedNetworkProcessingFailures,
                statistics.EventProcessingFailures);
            availability = MetricAvailability.Partial;
            reason = NetworkAvailabilityReason.CollectorError;
            detail = "One or more network events could not be parsed; retained values are lower bounds.";
        }

        NetworkEndpointSnapshotReader.EndpointSnapshot endpointSnapshot =
            availability is MetricAvailability.Available or MetricAvailability.Partial
                ? NetworkEndpointSnapshotReader.Read()
                : default;
        return new NetworkEventBatch(
            events,
            availability,
            reason,
            eventsLost,
            queueEventsDropped,
            statistics.UnattributedEvents,
            detail,
            statistics.EventsObserved,
            depthBeforeDrain,
            Volatile.Read(ref _networkMaximumQueueDepth),
            EtwBufferSizeMegabytes,
            endpointSnapshot.TcpConnections,
            endpointSnapshot.UdpEndpoints)
        {
            SendEvents = statistics.SendEvents,
            ReceiveEvents = statistics.ReceiveEvents,
            TcpSendEvents = statistics.TcpSendEvents,
            TcpReceiveEvents = statistics.TcpReceiveEvents,
            UdpSendEvents = statistics.UdpSendEvents,
            UdpReceiveEvents = statistics.UdpReceiveEvents,
            IPv4Events = statistics.IPv4Events,
            IPv6Events = statistics.IPv6Events,
            TotalSourceSendBytes = statistics.SourceSendBytes,
            TotalSourceReceiveBytes = statistics.SourceReceiveBytes,
            SystemProcessEvents = statistics.SystemProcessEvents,
            UnknownProcessEvents = statistics.UnknownProcessEvents,
            SessionStartFailures = Interlocked.Read(ref _sessionStartFailures),
            AccessDeniedFailures = Interlocked.Read(ref _accessDeniedFailures),
            EventProcessingFailures = statistics.EventProcessingFailures,
            UnsupportedEventVersions = statistics.UnsupportedEventVersions,
            QueueCapacity = NetworkEventQueueCapacity,
            LastSuccessfulEventTimestampUtc = statistics.LastSuccessfulEventTimestampUtc,
            CollectorStatus = availability
        };
    }

    internal static List<T> CreateDrainBuffer<T>(int depthBeforeDrain) =>
        new(Math.Max(0, depthBeforeDrain));

    public void Dispose()
    {
        Task? runTask;
        ETWTraceEventSource? source;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            runTask = _runTask;
            source = _source;
        }

        source?.StopProcessing();
        try
        {
            runTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(item => item is OperationCanceledException))
        {
        }

        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        ETWTraceEventSource? source;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            runTask = _runTask;
            source = _source;
        }

        source?.StopProcessing();
        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<MetricAvailability> EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if ((_runTask is null || _runTask.IsCompleted) && now >= _nextRetryUtc)
            {
                _started = new TaskCompletionSource<MetricAvailability>(TaskCreationOptions.RunContinuationsAsynchronously);
                _availability = MetricAvailability.WarmingUp;
                _networkAvailability = MetricAvailability.WarmingUp;
                _networkReason = NetworkAvailabilityReason.None;
                _detail = "Starting the kernel metric ETW session.";
                _networkDetail = _detail;
                _runTask = Task.Run(() => RunSession(_shutdown.Token), CancellationToken.None);
            }

            return _started?.Task ?? Task.FromResult(_availability);
        }
    }

    private void RunSession(CancellationToken cancellationToken)
    {
        bool sessionStarted = false;
        string operation = "TraceEventSession.StartTrace";
        try
        {
            // Session ownership stays explicit: never restart or replace a same-name session owned elsewhere.
            TraceEventSessionOptions options = TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate;
            using TraceEventSession session = new(SessionName, options)
            {
                StopOnDispose = true,
                BufferSizeMB = EtwBufferSizeMegabytes
            };
            operation = "TraceEventSession.EnableKernelProvider";
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DiskIO |
                KernelTraceEventParser.Keywords.DiskIOInit |
                KernelTraceEventParser.Keywords.Thread |
                KernelTraceEventParser.Keywords.NetworkTCPIP);
            lock (_gate)
            {
                _session = session;
                _source = session.Source;
            }

            KernelTraceEventParser parser = new(session.Source, KernelTraceEventParser.ParserTrackingOptions.None);
            parser.ThreadStartGroup += OnThreadStart;
            parser.ThreadEndGroup += OnThreadEnd;
            parser.DiskIOReadInit += OnDiskIoInit;
            parser.DiskIOWriteInit += OnDiskIoInit;
            parser.DiskIORead += data => OnDiskIo(data, PhysicalDiskOperation.Read);
            parser.DiskIOWrite += data => OnDiskIo(data, PhysicalDiskOperation.Write);
            try
            {
                AttachNetworkCallbacks(parser);
                SetNetworkStatus(MetricAvailability.Available, NetworkAvailabilityReason.None, null);
            }
            catch (Exception exception) when (IsRecoverableNetworkEventException(exception))
            {
                _networkStatistics.RecordProcessingFailure();
                SetNetworkStatus(
                    MetricAvailability.Error,
                    NetworkAvailabilityReason.CollectorError,
                    $"Network ETW callbacks could not be attached: {exception.GetType().Name}.");
            }
            SetStatus(MetricAvailability.Available, null);
            sessionStarted = true;
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((ETWTraceEventSource)state!).StopProcessing(),
                session.Source);
            session.Source.Process();
            Interlocked.Exchange(ref _etwEventsLost, Math.Max(0, session.EventsLost));

            if (!cancellationToken.IsCancellationRequested)
            {
                SetSessionFailure(
                    MetricAvailability.Error,
                    NetworkAvailabilityReason.CollectorError,
                    "The kernel metric ETW session ended unexpectedly.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            (MetricAvailability availability, NetworkAvailabilityReason reason, string detail) = ClassifyFailure(exception, operation);
            if (!sessionStarted)
            {
                Interlocked.Increment(ref _sessionStartFailures);
                if (availability == MetricAvailability.AccessDenied)
                {
                    Interlocked.Increment(ref _accessDeniedFailures);
                }
            }

            SetSessionFailure(availability, reason, detail);
        }
        finally
        {
            lock (_gate)
            {
                _session = null;
                _source = null;
            }
        }
    }

    private void OnThreadStart(ThreadTraceData data)
    {
        int threadId = data.ThreadID;
        int processId = data.ProcessID;
        if (threadId > 0 && processId > 0)
        {
            _threadProcesses.Set(threadId, processId);
        }
    }

    private void OnThreadEnd(ThreadTraceData data)
    {
        int threadId = data.ThreadID;
        if (threadId > 0)
        {
            _threadProcesses.Remove(threadId);
        }
    }

    private void OnDiskIo(DiskIOTraceData data, PhysicalDiskOperation operation)
    {
        Interlocked.Increment(ref _eventsObserved);
        int transferSize = Math.Max(0, data.TransferSize);
        if (operation == PhysicalDiskOperation.Read)
        {
            Interlocked.Increment(ref _readEventsObserved);
            SaturatingAdd(ref _readBytesObserved, transferSize);
        }
        else
        {
            Interlocked.Increment(ref _writeEventsObserved);
            SaturatingAdd(ref _writeBytesObserved, transferSize);
        }

        int threadId = ReadIssuingThreadId(data);
        // One initiating IRP can produce several split disk completions. Keep the bounded
        // correlation until a later init replaces it or normal eviction removes it.
        int processId = data.Irp > 0 && _irpProcesses.TryGetValue(data.Irp, out int initiatingProcessId)
            ? initiatingProcessId
            : 0;
        if (processId <= 0 && threadId > 0)
        {
            _threadProcesses.TryGetValue(threadId, out processId);
            if (processId <= 0)
            {
                processId = ResolveProcessIdFromThread(threadId);
                if (processId > 0)
                {
                    _threadProcesses.Set(threadId, processId);
                }
            }
        }

        if (processId <= 0)
        {
            processId = data.ProcessID;
        }

        if (processId <= 0)
        {
            Interlocked.Increment(ref _unattributedEvents);
            return;
        }

        PhysicalDiskIoEvent diskEvent = new(
            processId,
            Math.Max(0, threadId),
            EtwTimestampNormalizer.NormalizeToUtc(data.TimeStamp),
            operation,
            transferSize);
        if (_events.Writer.TryWrite(diskEvent))
        {
            UpdateMaximum(ref _maximumQueueDepth, _events.Reader.Count);
            UpdateLastSuccessfulEventTimestamp(diskEvent.TimestampUtc);
        }
        else
        {
            Interlocked.Increment(ref _queueEventsDropped);
        }
    }

    private void OnDiskIoInit(DiskIOInitTraceData data)
    {
        if (data.Irp == 0)
        {
            return;
        }

        int processId = data.ProcessID;
        if (processId <= 0 && data.ThreadID > 0)
        {
            _threadProcesses.TryGetValue(data.ThreadID, out processId);
            if (processId <= 0)
            {
                processId = ResolveProcessIdFromThread(data.ThreadID);
                if (processId > 0)
                {
                    _threadProcesses.Set(data.ThreadID, processId);
                }
            }
        }

        if (processId > 0)
        {
            _irpProcesses.Set(data.Irp, processId);
        }
    }

    private void OnNetwork<TEvent>(
        TEvent data,
        NetworkDirection direction,
        NetworkTransport transport,
        NetworkAddressFamily addressFamily,
        Func<TEvent, int> transferSizeReader)
        where TEvent : TraceEvent
    {
        if (!TryReadNetworkTransferSize(data, transferSizeReader, out int transferSize))
        {
            _networkStatistics.RecordMalformedEvent();
            return;
        }

        try
        {
            if (!_networkStatistics.TryRecord(direction, transport, addressFamily, transferSize))
            {
                return;
            }

            // TraceEvent's typed kernel network parsers copy the payload PID into
            // ProcessID during FixupData. Do not infer ownership from ThreadID.
            int processId = data.ProcessID;
            if (processId is 0 or SystemProcessId)
            {
                _networkStatistics.RecordSystemProcess();
                return;
            }

            if (processId < 0)
            {
                _networkStatistics.RecordUnknownProcess();
                return;
            }

            NetworkTrafficEvent networkEvent = new(
                processId,
                EtwTimestampNormalizer.NormalizeToUtc(data.TimeStamp),
                direction,
                transport,
                addressFamily,
                transferSize);
            if (_networkEvents.Writer.TryWrite(networkEvent))
            {
                UpdateMaximum(ref _networkMaximumQueueDepth, _networkEvents.Reader.Count);
                _networkStatistics.RecordSuccessfulEvent(networkEvent.TimestampUtc);
            }
            else
            {
                Interlocked.Increment(ref _networkQueueEventsDropped);
            }
        }
        catch (Exception exception) when (IsRecoverableNetworkEventException(exception))
        {
            _networkStatistics.RecordProcessingFailure();
        }
    }

    private void AttachNetworkCallbacks(KernelTraceEventParser parser)
    {
        parser.TcpIpSend += data => OnNetwork(
            data,
            NetworkDirection.Upload,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv4,
            static data => data.size);
        parser.TcpIpRecv += data => OnNetwork(
            data,
            NetworkDirection.Download,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv4,
            static data => data.size);
        parser.TcpIpSendIPV6 += data => OnNetwork(
            data,
            NetworkDirection.Upload,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv6,
            static data => data.size);
        parser.TcpIpRecvIPV6 += data => OnNetwork(
            data,
            NetworkDirection.Download,
            NetworkTransport.Tcp,
            NetworkAddressFamily.IPv6,
            static data => data.size);
        parser.UdpIpSend += data => OnNetwork(
            data,
            NetworkDirection.Upload,
            NetworkTransport.Udp,
            NetworkAddressFamily.IPv4,
            static data => data.size);
        parser.UdpIpRecv += data => OnNetwork(
            data,
            NetworkDirection.Download,
            NetworkTransport.Udp,
            NetworkAddressFamily.IPv4,
            static data => data.size);
        parser.UdpIpSendIPV6 += data => OnNetwork(
            data,
            NetworkDirection.Upload,
            NetworkTransport.Udp,
            NetworkAddressFamily.IPv6,
            static data => data.size);
        parser.UdpIpRecvIPV6 += data => OnNetwork(
            data,
            NetworkDirection.Download,
            NetworkTransport.Udp,
            NetworkAddressFamily.IPv6,
            static data => data.size);
    }

    private static bool IsRecoverableNetworkEventException(Exception exception) =>
        exception is ArgumentException
            or ArithmeticException
            or InvalidOperationException
            or IndexOutOfRangeException;

    internal static bool TryReadNetworkTransferSize<TEvent>(
        TEvent data,
        Func<TEvent, int> transferSizeReader,
        out int transferSize)
    {
        try
        {
            transferSize = transferSizeReader(data);
            return true;
        }
        catch (Exception exception) when (IsRecoverableNetworkEventException(exception))
        {
            transferSize = 0;
            return false;
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current = Volatile.Read(ref target);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static int ReadIssuingThreadId(DiskIOTraceData data)
    {
        foreach (string name in new[] { "IssuingThreadId", "IssuingThreadID" })
        {
            int index = data.PayloadIndex(name);
            if (index >= 0 && data.PayloadValue(index) is object value)
            {
                try
                {
                    return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
                {
                }
            }
        }

        return data.ThreadID;
    }

    private int ResolveProcessIdFromThread(int threadId)
    {
        nint threadHandle = OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
        if (threadHandle == 0)
        {
            Interlocked.Increment(ref _metadataLookupFailures);
            return 0;
        }

        try
        {
            uint processId = GetProcessIdOfThread(threadHandle);
            if (processId is 0 or > int.MaxValue)
            {
                Interlocked.Increment(ref _metadataLookupFailures);
                return 0;
            }

            return (int)processId;
        }
        finally
        {
            CloseHandle(threadHandle);
        }
    }

    private void SetStatus(MetricAvailability availability, string? detail)
    {
        lock (_gate)
        {
            _availability = availability;
            _detail = detail;
            _started?.TrySetResult(availability);
        }
    }

    private void SetNetworkStatus(
        MetricAvailability availability,
        NetworkAvailabilityReason reason,
        string? detail)
    {
        lock (_gate)
        {
            _networkAvailability = availability;
            _networkReason = reason;
            _networkDetail = detail;
        }
    }

    private void SetSessionFailure(
        MetricAvailability availability,
        NetworkAvailabilityReason reason,
        string detail)
    {
        lock (_gate)
        {
            _availability = availability;
            _networkAvailability = availability;
            _networkReason = reason;
            _detail = detail;
            _networkDetail = detail;
            _nextRetryUtc = DateTimeOffset.UtcNow + RetryDelay;
            _started?.TrySetResult(availability);
        }
    }

    internal static (MetricAvailability Availability, NetworkAvailabilityReason Reason, string Detail) ClassifyFailure(
        Exception exception,
        string operation)
    {
        Win32Exception? win32 = FindWin32Exception(exception);
        if (exception is UnauthorizedAccessException || win32?.NativeErrorCode == 5)
        {
            return (
                MetricAvailability.AccessDenied,
                NetworkAvailabilityReason.AccessDenied,
                $"Kernel metric ETW access was denied in {operation}. Win32 error 5 (ERROR_ACCESS_DENIED). Run with approved elevation or add the user to Performance Log Users.");
        }

        if (win32?.NativeErrorCode == 183 || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return (
                MetricAvailability.Unavailable,
                NetworkAvailabilityReason.SessionConflict,
                $"{operation} reported ERROR_ALREADY_EXISTS (183) for ETW session '{SessionName}'; Monitoring XS did not replace it.");
        }

        if (exception is PlatformNotSupportedException)
        {
            return (
                MetricAvailability.Unsupported,
                NetworkAvailabilityReason.Unsupported,
                "Kernel network ETW is not supported on this platform.");
        }

        if (win32?.NativeErrorCode is 8 or 14 or 1450)
        {
            return (
                MetricAvailability.Unavailable,
                NetworkAvailabilityReason.ResourceExhausted,
                "Kernel metric ETW could not start because Windows reported insufficient resources.");
        }

        Exception root = exception.GetBaseException();
        string message = root.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 300)
        {
            message = message[..300];
        }

        return (
            MetricAvailability.Error,
            NetworkAvailabilityReason.CollectorError,
            $"Kernel metric ETW could not start: {root.GetType().Name}: {message}");
    }

    private static Win32Exception? FindWin32Exception(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32)
            {
                return win32;
            }
        }

        return null;
    }

    private DateTimeOffset? ReadLastSuccessfulEventTimestamp()
    {
        long ticks = Interlocked.Read(ref _lastSuccessfulEventTimestampUtcTicks);
        return ticks <= 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private void UpdateLastSuccessfulEventTimestamp(DateTimeOffset timestampUtc)
    {
        long candidate = timestampUtc.UtcDateTime.Ticks;
        long current = Interlocked.Read(ref _lastSuccessfulEventTimestampUtcTicks);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(
                ref _lastSuccessfulEventTimestampUtcTicks,
                candidate,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static void SaturatingAdd(ref long target, int value)
    {
        long current = Interlocked.Read(ref target);
        while (current < long.MaxValue)
        {
            long candidate = long.MaxValue - current < value ? long.MaxValue : current + value;
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static ulong ToUnsigned(long value) => value <= 0 ? 0UL : (ulong)value;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessIdOfThread(nint threadHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private sealed class BoundedThreadProcessMap
    {
        private readonly int _capacity;
        private readonly Dictionary<int, LinkedListNode<Entry>> _entries;
        private readonly LinkedList<Entry> _age = new();
        private readonly object _gate = new();

        public BoundedThreadProcessMap(int capacity)
        {
            _capacity = capacity;
            _entries = new Dictionary<int, LinkedListNode<Entry>>(capacity);
        }

        public void Set(int threadId, int processId)
        {
            lock (_gate)
            {
                if (_entries.Remove(threadId, out LinkedListNode<Entry>? previous))
                {
                    _age.Remove(previous);
                }

                LinkedListNode<Entry> node = _age.AddLast(new Entry(threadId, processId));
                _entries.Add(threadId, node);
                if (_entries.Count > _capacity)
                {
                    LinkedListNode<Entry> oldest = _age.First!;
                    _age.RemoveFirst();
                    _entries.Remove(oldest.Value.ThreadId);
                }
            }
        }

        public bool TryGetValue(int threadId, out int processId)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(threadId, out LinkedListNode<Entry>? node))
                {
                    processId = node.Value.ProcessId;
                    return true;
                }

                processId = 0;
                return false;
            }
        }

        public void Remove(int threadId)
        {
            lock (_gate)
            {
                if (_entries.Remove(threadId, out LinkedListNode<Entry>? node))
                {
                    _age.Remove(node);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _age.Clear();
            }
        }

        private sealed record Entry(int ThreadId, int ProcessId);
    }

    internal sealed class BoundedIrpProcessMap
    {
        private readonly int _capacity;
        private readonly Dictionary<ulong, LinkedListNode<Entry>> _entries;
        private readonly LinkedList<Entry> _age = new();
        private readonly object _gate = new();

        public BoundedIrpProcessMap(int capacity)
        {
            _capacity = capacity;
            _entries = new Dictionary<ulong, LinkedListNode<Entry>>(capacity);
        }

        public void Set(ulong irp, int processId)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(irp, out LinkedListNode<Entry>? existing)
                    && existing.Value.ProcessId != SystemProcessId
                    && processId == SystemProcessId)
                {
                    return;
                }

                if (_entries.Remove(irp, out LinkedListNode<Entry>? previous))
                {
                    _age.Remove(previous);
                }

                LinkedListNode<Entry> node = _age.AddLast(new Entry(irp, processId));
                _entries.Add(irp, node);
                if (_entries.Count > _capacity)
                {
                    LinkedListNode<Entry> oldest = _age.First!;
                    _age.RemoveFirst();
                    _entries.Remove(oldest.Value.Irp);
                }
            }
        }

        public bool TryGetValue(ulong irp, out int processId)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(irp, out LinkedListNode<Entry>? node))
                {
                    processId = node.Value.ProcessId;
                    return true;
                }

                processId = 0;
                return false;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _age.Clear();
            }
        }

        private sealed record Entry(ulong Irp, int ProcessId);
    }
}
