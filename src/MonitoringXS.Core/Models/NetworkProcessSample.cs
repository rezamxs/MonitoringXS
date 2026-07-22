namespace MonitoringXS.Core.Models;

public sealed record NetworkProcessSample(
    ProcessInstanceId Process,
    DateTimeOffset CapturedAtUtc,
    MetricValue<double> DownloadBytesPerSecond,
    MetricValue<double> UploadBytesPerSecond,
    MetricValue<ulong> SessionDownloadedBytes,
    MetricValue<ulong> SessionUploadedBytes,
    MetricValue<int> ActiveTcpConnectionCount,
    MetricValue<int> UdpEndpointCount,
    NetworkCollectorDiagnostics Diagnostics);
