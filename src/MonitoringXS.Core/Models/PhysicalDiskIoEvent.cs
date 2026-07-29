namespace MonitoringXS.Core.Models;

public sealed record PhysicalDiskIoEvent
{
    public PhysicalDiskIoEvent(
        int processId,
        int threadId,
        DateTimeOffset timestampUtc,
        PhysicalDiskOperation operation,
        int transferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegative(threadId);
        ArgumentOutOfRangeException.ThrowIfNegative(transferSize);

        ProcessId = processId;
        ThreadId = threadId;
        TimestampUtc = timestampUtc.ToUniversalTime();
        Operation = operation;
        TransferSize = transferSize;
    }

    public int ProcessId { get; }

    public int ThreadId { get; }

    // QPC-relative values never cross the Windows platform boundary. This value is always UTC.
    public DateTimeOffset TimestampUtc { get; }

    public PhysicalDiskOperation Operation { get; }

    public int TransferSize { get; }
}
