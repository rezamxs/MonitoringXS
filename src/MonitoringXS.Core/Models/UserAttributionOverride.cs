namespace MonitoringXS.Core.Models;

public sealed record UserAttributionOverride(
    string ExecutablePath,
    string LogicalApplicationId,
    string DisplayName,
    string? Publisher,
    ApplicationDisposition Disposition,
    DateTimeOffset UpdatedAt);

public sealed record UserAttributionOverrideSnapshot(
    IReadOnlyDictionary<string, UserAttributionOverride> Overrides,
    bool IsAvailable,
    string? Error);

public sealed record OverrideMutationResult(bool Succeeded, string? Error)
{
    public static OverrideMutationResult Success { get; } = new(true, null);

    public static OverrideMutationResult Failure(string error) => new(false, error);
}
