using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Metrics;

internal sealed class NetworkEventStatistics
{
    private long _eventsObserved;
    private long _sendEvents;
    private long _receiveEvents;
    private long _tcpSendEvents;
    private long _tcpReceiveEvents;
    private long _udpSendEvents;
    private long _udpReceiveEvents;
    private long _ipv4Events;
    private long _ipv6Events;
    private long _sourceSendBytes;
    private long _sourceReceiveBytes;
    private long _unattributedEvents;
    private long _systemProcessEvents;
    private long _unknownProcessEvents;
    private long _eventProcessingFailures;
    private long _unsupportedEventVersions;
    private long _lastSuccessfulEventTimestampUtcTicks;

    public bool TryRecord(
        NetworkDirection direction,
        NetworkTransport transport,
        NetworkAddressFamily addressFamily,
        int transferSize)
    {
        Interlocked.Increment(ref _eventsObserved);
        if (transferSize < 0)
        {
            Interlocked.Increment(ref _eventProcessingFailures);
            Interlocked.Increment(ref _unattributedEvents);
            return false;
        }

        if (direction == NetworkDirection.Upload)
        {
            Interlocked.Increment(ref _sendEvents);
            SaturatingAdd(ref _sourceSendBytes, transferSize);
            if (transport == NetworkTransport.Tcp)
            {
                Interlocked.Increment(ref _tcpSendEvents);
            }
            else
            {
                Interlocked.Increment(ref _udpSendEvents);
            }
        }
        else
        {
            Interlocked.Increment(ref _receiveEvents);
            SaturatingAdd(ref _sourceReceiveBytes, transferSize);
            if (transport == NetworkTransport.Tcp)
            {
                Interlocked.Increment(ref _tcpReceiveEvents);
            }
            else
            {
                Interlocked.Increment(ref _udpReceiveEvents);
            }
        }

        if (addressFamily == NetworkAddressFamily.IPv4)
        {
            Interlocked.Increment(ref _ipv4Events);
        }
        else
        {
            Interlocked.Increment(ref _ipv6Events);
        }
        return true;
    }

    public void RecordSystemProcess()
    {
        Interlocked.Increment(ref _systemProcessEvents);
        Interlocked.Increment(ref _unattributedEvents);
    }

    public void RecordUnknownProcess()
    {
        Interlocked.Increment(ref _unknownProcessEvents);
        Interlocked.Increment(ref _unattributedEvents);
    }

    public void RecordProcessingFailure()
    {
        Interlocked.Increment(ref _eventProcessingFailures);
        Interlocked.Increment(ref _unattributedEvents);
    }

    public void RecordMalformedEvent()
    {
        Interlocked.Increment(ref _eventsObserved);
        RecordProcessingFailure();
    }

    public void RecordUnsupportedEventVersion()
    {
        Interlocked.Increment(ref _unsupportedEventVersions);
        Interlocked.Increment(ref _unattributedEvents);
    }

    public void RecordSuccessfulEvent(DateTimeOffset timestampUtc)
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

    public Snapshot Read()
    {
        long timestampTicks = Interlocked.Read(ref _lastSuccessfulEventTimestampUtcTicks);
        return new Snapshot(
            Interlocked.Read(ref _eventsObserved),
            Interlocked.Read(ref _sendEvents),
            Interlocked.Read(ref _receiveEvents),
            Interlocked.Read(ref _tcpSendEvents),
            Interlocked.Read(ref _tcpReceiveEvents),
            Interlocked.Read(ref _udpSendEvents),
            Interlocked.Read(ref _udpReceiveEvents),
            Interlocked.Read(ref _ipv4Events),
            Interlocked.Read(ref _ipv6Events),
            ToUnsigned(Interlocked.Read(ref _sourceSendBytes)),
            ToUnsigned(Interlocked.Read(ref _sourceReceiveBytes)),
            Interlocked.Read(ref _unattributedEvents),
            Interlocked.Read(ref _systemProcessEvents),
            Interlocked.Read(ref _unknownProcessEvents),
            Interlocked.Read(ref _eventProcessingFailures),
            Interlocked.Read(ref _unsupportedEventVersions),
            timestampTicks > 0 ? new DateTimeOffset(timestampTicks, TimeSpan.Zero) : null);
    }

    private static void SaturatingAdd(ref long target, int value)
    {
        long current = Interlocked.Read(ref target);
        while (true)
        {
            long next = long.MaxValue - current < value ? long.MaxValue : current + value;
            long observed = Interlocked.CompareExchange(ref target, next, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static ulong ToUnsigned(long value) => value <= 0 ? 0 : (ulong)value;

    internal readonly record struct Snapshot(
        long EventsObserved,
        long SendEvents,
        long ReceiveEvents,
        long TcpSendEvents,
        long TcpReceiveEvents,
        long UdpSendEvents,
        long UdpReceiveEvents,
        long IPv4Events,
        long IPv6Events,
        ulong SourceSendBytes,
        ulong SourceReceiveBytes,
        long UnattributedEvents,
        long SystemProcessEvents,
        long UnknownProcessEvents,
        long EventProcessingFailures,
        long UnsupportedEventVersions,
        DateTimeOffset? LastSuccessfulEventTimestampUtc);
}
