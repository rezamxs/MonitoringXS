using System.Text.Json.Serialization;

namespace MonitoringXS.Core.Models;

public readonly record struct ProcessInstanceId
{
    [JsonConstructor]
    public ProcessInstanceId(int processId, DateTimeOffset startTimeUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ProcessId = processId;
        StartTimeUtc = startTimeUtc.ToUniversalTime();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartTimeUtc { get; }

    public override string ToString() => $"{ProcessId}@{StartTimeUtc:O}";
}
