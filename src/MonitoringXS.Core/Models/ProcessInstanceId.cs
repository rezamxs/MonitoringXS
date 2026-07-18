namespace MonitoringXS.Core.Models;

public readonly record struct ProcessInstanceId(int ProcessId, DateTimeOffset StartTimeUtc)
{
    public override string ToString() => $"{ProcessId}@{StartTimeUtc:O}";
}
