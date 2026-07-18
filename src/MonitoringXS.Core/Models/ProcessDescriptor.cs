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
    public string NormalizedProcessName => ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? ProcessName[..^4]
        : ProcessName;
}
