namespace MonitoringXS.Core.Models;

public enum ProcessDiscoveryIssueKind
{
    AccessDenied,
    ProcessExited,
    DescriptorUnavailable,
    ExecutablePathUnavailable,
    MetadataUnavailable
}

public sealed record ProcessDiscoveryIssue(
    int ProcessId,
    ProcessDiscoveryIssueKind Kind,
    string? Detail = null);

/// <summary>
/// Result of one successful base process enumeration. Cancellation and fatal
/// enumeration failures are reported by exceptions and never masquerade as an empty result.
/// </summary>
public sealed record ProcessDiscoverySnapshot(
    IReadOnlyList<int> ObservedProcessIds,
    IReadOnlyList<ProcessDescriptor> Processes,
    IReadOnlyList<ProcessDiscoveryIssue> Issues)
{
    public bool IsPartial => Issues.Count > 0;
}
