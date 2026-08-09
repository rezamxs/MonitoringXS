namespace MonitoringXS.Core.Models;

public enum ApplicationTheme
{
    System,
    Light,
    Dark
}

public enum ApplicationLanguage
{
    System,
    English,
    Persian
}

public enum ApplicationSortPreference
{
    Name,
    Cpu,
    Memory,
    ProcessIo,
    PhysicalDisk,
    Network,
    Gpu
}

public sealed record ApplicationSettings(
    int Version,
    int LiveSamplingSeconds,
    int HistoryRetentionHours,
    ApplicationTheme Theme,
    ApplicationLanguage Language = ApplicationLanguage.System,
    ApplicationSortPreference ApplicationSort = ApplicationSortPreference.Name,
    bool ApplicationSortDescending = false)
{
    public const int CurrentVersion = 1;

    public static ApplicationSettings Default { get; } = new(
        CurrentVersion,
        1,
        24,
        ApplicationTheme.System,
        ApplicationLanguage.System,
        ApplicationSortPreference.Name,
        false);

    public TimeSpan LiveSamplingInterval => TimeSpan.FromSeconds(LiveSamplingSeconds);

    public TimeSpan HistoryRetention => TimeSpan.FromHours(HistoryRetentionHours);

    public bool IsValid =>
        Version == CurrentVersion
        && LiveSamplingSeconds is 1 or 2 or 5
        && HistoryRetentionHours is 6 or 24 or 72 or 168
        && Enum.IsDefined(Theme)
        && Enum.IsDefined(Language)
        && Enum.IsDefined(ApplicationSort);
}

public sealed record ApplicationSettingsLoadResult(
    ApplicationSettings Settings,
    bool IsAvailable,
    bool Recovered,
    string? Error = null);

public sealed record ApplicationSettingsSaveResult(
    bool Succeeded,
    string? Error = null)
{
    public static ApplicationSettingsSaveResult Success { get; } = new(true);
}

public sealed record MetricHistoryRetentionResult(
    bool Succeeded,
    string? Error = null)
{
    public static MetricHistoryRetentionResult Success { get; } = new(true);
}
