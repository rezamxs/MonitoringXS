namespace MonitoringXS.Core.Models;

public sealed record NetworkMetricSet(
    MetricValue<double> DownloadBytesPerSecond,
    MetricValue<double> UploadBytesPerSecond,
    MetricValue<ulong> SessionDownloadedBytes,
    MetricValue<ulong> SessionUploadedBytes,
    MetricValue<int> ActiveTcpConnectionCount,
    MetricValue<int> UdpEndpointCount,
    NetworkCollectorDiagnostics Diagnostics)
{
    public MetricAvailability Availability => DownloadBytesPerSecond.Availability;

    public NetworkAvailabilityReason Reason => Diagnostics.Reason;

    public static NetworkMetricSet Unavailable(
        MetricAvailability availability,
        NetworkAvailabilityReason reason,
        string? detail = null) => new(
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<int>.Unavailable(availability, detail),
        MetricValue<int>.Unavailable(availability, detail),
        new NetworkCollectorDiagnostics(reason, 0, 0, 0, 0, 0, 0, 0, 0, 0, false));
}
