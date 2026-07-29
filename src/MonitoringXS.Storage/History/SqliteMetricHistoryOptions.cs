namespace MonitoringXS.Storage.History;

public sealed record SqliteMetricHistoryOptions
{
    public SqliteMetricHistoryOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan RawSampleRetention { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan DownsampleBucket { get; init; } = TimeSpan.FromMinutes(5);

    public int QueueCapacity { get; init; } = 256;

    public int BatchSize { get; init; } = 32;

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(1);

    public long MaximumDatabaseBytes { get; init; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        if (Retention <= TimeSpan.Zero
            || RawSampleRetention <= TimeSpan.Zero
            || RawSampleRetention >= Retention
            || DownsampleBucket <= TimeSpan.Zero
            || QueueCapacity < 1
            || BatchSize < 1
            || CleanupInterval <= TimeSpan.Zero
            || MaximumDatabaseBytes < 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(SqliteMetricHistoryOptions));
        }
    }
}
