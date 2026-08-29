using System.Globalization;
using System.Text;
using MonitoringXS.App.Localization;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

internal static class ProcessDetailsTextFormatter
{
    public static string Format(
        ApplicationMetricSnapshot snapshot,
        ProcessDescriptor process,
        DateTimeOffset timestamp,
        LocalizationService localization)
    {
        StringBuilder text = new();
        ProcessMetricSample? metrics = snapshot.ProcessMetrics.GetValueOrDefault(process.InstanceId);
        MetricValue<double>? cpu = metrics?.CpuPercent
            ?? (snapshot.ProcessCount == 1 ? snapshot.CpuPercent : null);
        MetricValue<long>? memory = metrics?.WorkingSetBytes
            ?? (snapshot.ProcessCount == 1 ? snapshot.WorkingSetBytes : null);
        Append(text, localization.Get(LocalizationKeys.ProcessApplication), snapshot.Application.DisplayName);
        Append(text, localization.Get(LocalizationKeys.ProcessName), process.NormalizedProcessName);
        Append(text, localization.Get(LocalizationKeys.ProcessId), process.InstanceId.ProcessId.ToString(CultureInfo.InvariantCulture));
        Append(text, localization.Get(LocalizationKeys.ProcessStatus), localization.Get(LocalizationKeys.ProcessRunning));
        Append(text, localization.Get(LocalizationKeys.ProcessArchitecture), Architecture(process.Architecture, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessExecutablePath), Value(process.ExecutablePath, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessPublisher), Value(process.Publisher, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessFileVersion), Value(process.FileVersion, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessStartTime), process.InstanceId.StartTimeUtc.ToString("O", CultureInfo.InvariantCulture));
        Append(text, localization.Get(LocalizationKeys.ProcessRunningDuration), Duration(timestamp - process.InstanceId.StartTimeUtc));
        Append(text, localization.Get(LocalizationKeys.ProcessCpu), Percent(cpu, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessMemory), Bytes(memory, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessThreads), Count(process.ThreadCount, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessHandles), Count(process.HandleCount, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessParentPid), process.ParentProcessId?.ToString(CultureInfo.InvariantCulture) ?? localization.Get(LocalizationKeys.Unavailable));
        Append(text, localization.Get(LocalizationKeys.ProcessParent), Value(process.ParentProcessName, localization));
        Append(text, localization.Get(LocalizationKeys.ProcessIdentity), $"{process.InstanceId.ProcessId} + {process.InstanceId.StartTimeUtc:O}");
        return text.ToString();
    }

    private static void Append(StringBuilder text, string label, string? value = null)
    {
        if (value is null)
        {
            text.AppendLine(label);
            return;
        }

        text.Append(label).Append(": ").AppendLine(value);
    }

    internal static string Architecture(ProcessArchitecture architecture, LocalizationService localization) => architecture switch
    {
        ProcessArchitecture.X86 => localization.Get(LocalizationKeys.ProcessArchitectureX86),
        ProcessArchitecture.X64 => localization.Get(LocalizationKeys.ProcessArchitectureX64),
        ProcessArchitecture.Arm64 => localization.Get(LocalizationKeys.ProcessArchitectureArm64),
        _ => localization.Get(LocalizationKeys.ProcessArchitectureUnknown)
    };

    internal static string Value(string? value, LocalizationService localization) =>
        string.IsNullOrWhiteSpace(value) ? localization.Get(LocalizationKeys.Unavailable) : value;

    internal static string Count(MetricValue<int> metric, LocalizationService localization) =>
        metric.IsAvailable ? metric.Value!.Value.ToString(CultureInfo.InvariantCulture) : localization.Get(LocalizationKeys.Unavailable);

    private static string Percent(MetricValue<double>? metric, LocalizationService localization) =>
        metric is { IsAvailable: true } value
            ? $"{Partial(value, localization)}{value.Value!.Value.ToString("0.0", CultureInfo.InvariantCulture)}% ({value.Availability})"
            : localization.Get(LocalizationKeys.Unavailable);

    private static string Bytes(MetricValue<long>? metric, LocalizationService localization) =>
        metric is { IsAvailable: true } value
            ? $"{Partial(value, localization)}{FormatBytes(value.Value!.Value)} ({value.Availability})"
            : localization.Get(LocalizationKeys.Unavailable);

    private static string FormatBytes(double bytes) => bytes >= 1024d * 1024d * 1024d
        ? $"{(bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture)} GB"
        : bytes >= 1024d * 1024d
            ? $"{(bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture)} MB"
            : bytes >= 1024d
                ? $"{(bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB"
                : $"{bytes.ToString("0", CultureInfo.InvariantCulture)} B";

    private static string Duration(TimeSpan duration) => duration < TimeSpan.Zero
        ? "0:00:00"
        : duration.ToString("d'.'hh':'mm':'ss", CultureInfo.InvariantCulture);

    private static string Partial<T>(MetricValue<T> metric, LocalizationService localization)
        where T : struct => metric.IsComplete
            ? string.Empty
            : localization.Get(LocalizationKeys.AtLeast) + " ";
}
