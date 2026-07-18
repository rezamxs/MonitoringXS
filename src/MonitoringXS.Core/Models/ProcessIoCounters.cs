namespace MonitoringXS.Core.Models;

public readonly record struct ProcessIoCounters(
    ulong ReadOperationCount,
    ulong WriteOperationCount,
    ulong OtherOperationCount,
    ulong ReadBytes,
    ulong WriteBytes,
    ulong OtherBytes);
