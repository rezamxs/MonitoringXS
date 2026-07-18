namespace MonitoringXS.Core.Models;

public enum ApplicationDisposition
{
    Installed,
    Packaged,
    Portable,
    System,
    Unresolved
}

public enum ClassificationConfidence
{
    Low,
    Medium,
    High,
    Certain
}

public sealed record ApplicationIdentity(
    string LogicalApplicationId,
    string DisplayName,
    string? Publisher,
    ApplicationDisposition Disposition,
    string? InstallationPath,
    ClassificationConfidence Confidence,
    string ClassificationReason);
