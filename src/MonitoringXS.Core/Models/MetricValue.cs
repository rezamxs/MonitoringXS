namespace MonitoringXS.Core.Models;

public readonly record struct MetricValue<T>(T? Value, MetricAvailability Availability, string? Detail = null)
    where T : struct
{
    public bool IsAvailable => Availability is MetricAvailability.Available or MetricAvailability.Partial && Value.HasValue;

    public bool IsComplete => Availability == MetricAvailability.Available && Value.HasValue;

    public static MetricValue<T> Available(T value) => new(value, MetricAvailability.Available);

    public static MetricValue<T> Partial(T value, string detail) => new(value, MetricAvailability.Partial, detail);

    public static MetricValue<T> Unavailable(MetricAvailability availability, string? detail = null) =>
        new(null, availability, detail);
}
