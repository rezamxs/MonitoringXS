namespace MonitoringXS.Core.Models;

public sealed record ProcessDescriptor(
    ProcessInstanceId InstanceId,
    string ProcessName,
    string? ExecutablePath,
    string? ProductName,
    string? FileDescription,
    string? Publisher,
    string? MainWindowTitle,
    int? ParentProcessId,
    bool IsServiceSession,
    bool HasVisibleWindow)
{
    public ProcessArchitecture Architecture { get; init; } = ProcessArchitecture.Unknown;

    public MetricValue<int> ThreadCount { get; init; } =
        MetricValue<int>.Unavailable(MetricAvailability.Unavailable);

    public MetricValue<int> HandleCount { get; init; } =
        MetricValue<int>.Unavailable(MetricAvailability.Unavailable);

    public string? ParentProcessName { get; init; }

    public string? FileVersion { get; init; }

    public string NormalizedProcessName => ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? ProcessName[..^4]
        : ProcessName;
}
