namespace MonitoringXS.Platform.Windows.Metrics;

public static class EtwTimestampNormalizer
{
    public static DateTimeOffset NormalizeToUtc(DateTime timestamp)
    {
        DateTime normalized = timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Local)
            : timestamp;
        return new DateTimeOffset(normalized).ToUniversalTime();
    }
}
