namespace MonitoringXS.Core.Models;

public sealed record NetworkTrafficEvent
{
    public NetworkTrafficEvent(
        int processId,
        DateTimeOffset timestampUtc,
        NetworkDirection direction,
        NetworkTransport transport,
        NetworkAddressFamily addressFamily,
        int transferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegative(transferSize);

        ProcessId = processId;
        TimestampUtc = timestampUtc.ToUniversalTime();
        Direction = direction;
        Transport = transport;
        AddressFamily = addressFamily;
        TransferSize = transferSize;
    }

    public int ProcessId { get; }

    // Raw QPC-relative values never cross the Windows platform boundary.
    public DateTimeOffset TimestampUtc { get; }

    public NetworkDirection Direction { get; }

    public NetworkTransport Transport { get; }

    public NetworkAddressFamily AddressFamily { get; }

    public int TransferSize { get; }
}
