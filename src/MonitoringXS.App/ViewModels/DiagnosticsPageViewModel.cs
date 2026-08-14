using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using MonitoringXS.App.Localization;
using MonitoringXS.Application;
using MonitoringXS.Core.Models;
using MonitoringXS.Storage.History;

namespace MonitoringXS.App.ViewModels;

public sealed record DiagnosticItem(
    string Label,
    string Value,
    string? Detail = null,
    bool IsTechnicalValue = false,
    bool IncludeInSafeSummary = true)
{
    public FlowDirection ValueFlowDirection { get; init; } = FlowDirection.LeftToRight;
}

/// <summary>
/// Beginner-friendly status row for the Diagnostics summary.
/// </summary>
public sealed record BeginnerStatusRow(
    string Label,
    string Status,
    string Explanation,
    Microsoft.UI.Xaml.Media.SolidColorBrush StatusForeground);

public sealed partial class DiagnosticsPageViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly SettingsPageViewModel _settings;
    private readonly SqliteMetricHistoryStore _history;
    private readonly LiveRefreshCadence _cadence;
    private readonly IClipboardService _clipboard;
    private readonly LocalizationService _localization;
    private readonly MetricExplanationService _metricExplanations;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private bool _disposed;

    public DiagnosticsPageViewModel(
        MainWindowViewModel main,
        SettingsPageViewModel settings,
        SqliteMetricHistoryStore history,
        LiveRefreshCadence cadence,
        IClipboardService clipboard,
        LocalizationService localization,
        MetricExplanationService metricExplanations)
    {
        _main = main;
        _settings = settings;
        _history = history;
        _cadence = cadence;
        _clipboard = clipboard;
        _localization = localization;
        _metricExplanations = metricExplanations;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CopySafeSummaryCommand = new AsyncRelayCommand(CopySafeSummaryAsync);
        _localization.LanguageChanged += Localization_LanguageChanged;
        Rebuild();
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand CopySafeSummaryCommand { get; }

    public ObservableCollection<DiagnosticItem> ApplicationItems { get; } = [];

    public ObservableCollection<DiagnosticItem> CollectorItems { get; } = [];

    public ObservableCollection<DiagnosticItem> ServiceItems { get; } = [];

    public ObservableCollection<DiagnosticItem> StorageItems { get; } = [];

    public ObservableCollection<DiagnosticItem> AdvancedItems { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; }

    [ObservableProperty]
    public partial string BeginnerSummaryText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<BeginnerStatusRow> BeginnerStatusRows { get; set; }

    [ObservableProperty]
    public partial string CopyStatusText { get; set; }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsRefreshing = true;
        try
        {
            await _settings.RefreshBrokerStatusAsync(cancellationToken);
            Rebuild();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task CopySafeSummaryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string summary = BuildSafeSummary(
            _localization.Get("DiagnosticsSafeSummaryTitle"),
            SummaryText,
            ApplicationItems,
            CollectorItems,
            ServiceItems,
            StorageItems);
        bool copied = await _clipboard.CopyTextAsync(summary, cancellationToken);
        CopyStatusText = _localization.Get(
            copied ? "DiagnosticsCopied" : "DiagnosticsCopyFailed");
    }

    internal static string BuildSafeSummary(
        string title,
        string summary,
        params IEnumerable<DiagnosticItem>[] sections)
    {
        StringBuilder text = new();
        text.AppendLine(title);
        text.AppendLine(summary);
        foreach (DiagnosticItem item in sections.SelectMany(section => section)
            .Where(item => item.IncludeInSafeSummary))
        {
            text.Append(item.Label).Append(": ").AppendLine(item.Value);
        }

        return text.ToString().TrimEnd();
    }

    private void Rebuild()
    {
        MonitoringSnapshot? dashboard = _main.LatestDashboardSnapshot;
        ApplicationMetricSnapshot[] applications = dashboard is null
            ? []
            : dashboard.Applications.ToArray();
        MetricHistoryStoreDiagnostics history = _history.Diagnostics;

        MetricAvailability cpu = Aggregate(applications.Select(item => item.CpuPercent.Availability));
        MetricAvailability memory = Aggregate(applications.Select(item => item.WorkingSetBytes.Availability));
        MetricAvailability processIo = Aggregate(applications.SelectMany(item => new[]
        {
            item.IoReadBytesPerSecond.Availability,
            item.IoWriteBytesPerSecond.Availability
        }));
        MetricAvailability physicalDisk = Aggregate(applications.SelectMany(item => new[]
        {
            item.PhysicalDisk.ReadBytesPerSecond.Availability,
            item.PhysicalDisk.WriteBytesPerSecond.Availability
        }));
        MetricAvailability network = Aggregate(applications.SelectMany(item => new[]
        {
            item.Network.DownloadBytesPerSecond.Availability,
            item.Network.UploadBytesPerSecond.Availability
        }));
        MetricAvailability gpu = Aggregate(applications.Select(item => item.Gpu.UtilizationPercent.Availability));
        MetricAvailability historyAvailability = history.WriteFailures > 0
            ? MetricAvailability.Error
            : history.QueueDrops > 0
                ? MetricAvailability.Partial
                : MetricAvailability.Available;
        bool storageHealthy = historyAvailability == MetricAvailability.Available;
        bool allHealthy = applications.Length > 0 &&
            new[] { cpu, memory, processIo, physicalDisk, network, gpu }
                .All(state => state == MetricAvailability.Available) && storageHealthy;
        SummaryText = applications.Length == 0
            ? _localization.Get("DiagnosticsWaitingForSnapshot")
            : allHealthy
                ? _localization.Get("DiagnosticsHealthySummary")
                : _localization.Get("DiagnosticsDegradedSummary");

        // Build beginner-friendly summary
        BeginnerSummaryText = applications.Length == 0
            ? _localization.Get("DiagnosticsWaitingForSnapshot")
            : allHealthy
                ? _localization.Get("DiagnosticsBeginnerAllHealthy")
                : _localization.Get("DiagnosticsBeginnerAttentionNeeded");

        var greenBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
        var orangeBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
        var grayBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

        bool resourceMonitoringHealthy = new[] { cpu, memory, processIo }.All(s => s == MetricAvailability.Available);
        bool advancedServiceHealthy = new[] { physicalDisk, network }.All(s => s == MetricAvailability.Available);

        BeginnerStatusRows =
        [
            new BeginnerStatusRow(
                _localization.Get("DiagnosticsBeginnerMonitoringXS"),
                _localization.Get("DiagnosticsStatusWorking"),
                _localization.Get("DiagnosticsExplanationMonitoringXS"),
                greenBrush),
            new BeginnerStatusRow(
                _localization.Get("DiagnosticsBeginnerResourceMonitoring"),
                resourceMonitoringHealthy
                    ? _localization.Get("DiagnosticsStatusWorking")
                    : _localization.Get("DiagnosticsStatusAttention"),
                _localization.Get("DiagnosticsExplanationResourceMonitoring"),
                resourceMonitoringHealthy ? greenBrush : orangeBrush),
            new BeginnerStatusRow(
                _localization.Get("DiagnosticsBeginnerAdvancedService"),
                advancedServiceHealthy
                    ? _localization.Get("DiagnosticsStatusWorking")
                    : _settings.BrokerState is null
                        ? _localization.Get("DiagnosticsStatusUnavailable")
                        : _localization.Get("DiagnosticsStatusAttention"),
                _localization.Get("DiagnosticsExplanationAdvancedService"),
                advancedServiceHealthy ? greenBrush : (_settings.BrokerState is null ? grayBrush : orangeBrush)),
            new BeginnerStatusRow(
                _localization.Get("DiagnosticsBeginnerHistory"),
                storageHealthy
                    ? _localization.Get("DiagnosticsStatusWorking")
                    : _localization.Get("DiagnosticsStatusAttention"),
                _localization.Get("DiagnosticsExplanationHistory"),
                storageHealthy ? greenBrush : orangeBrush)
        ];

        Replace(ApplicationItems,
        [
            Item("DiagnosticsVersion", DisplayVersion(), technical: true),
            Item("DiagnosticsBuild", BuildConfiguration(), technical: true),
            Item("DiagnosticsArchitecture", RuntimeInformation.ProcessArchitecture.ToString(), technical: true),
            Item("DiagnosticsWindows", Environment.OSVersion.VersionString, technical: true),
            Item("DiagnosticsWindowsAppSdk", typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version?.ToString()
                ?? _localization.Get(LocalizationKeys.Unavailable), technical: true),
            Item("DiagnosticsLanguage", _localization.Culture.Name, technical: true),
            Item("DiagnosticsFlowDirection", _localization.Direction.ToString(), technical: true),
            Item("DiagnosticsSampling", _localization.Format("DiagnosticsSeconds", _cadence.Interval.TotalSeconds)),
            Item("DiagnosticsUptime", FormatDuration(Stopwatch.GetElapsedTime(_startedAt)))
        ]);

        Replace(CollectorItems,
        [
            CollectorItem("DiagnosticsCpuMemory", Combine(cpu, memory), MetricDescriptionId.Cpu),
            CollectorItem("DiagnosticsProcessIo", processIo, MetricDescriptionId.ProcessIo),
            CollectorItem("DiagnosticsPhysicalDisk", physicalDisk, MetricDescriptionId.PhysicalDisk),
            CollectorItem("DiagnosticsNetwork", network, MetricDescriptionId.Network),
            CollectorItem("DiagnosticsGpu", gpu, MetricDescriptionId.Gpu),
            Item(
                "DiagnosticsHistoryStatus",
                AvailabilityText(historyAvailability),
                _metricExplanations.Reason(MetricDescriptionId.History, historyAvailability))
        ]);

        Replace(ServiceItems,
        [
            Item("DiagnosticsServiceState", _settings.BrokerState is null
                ? _localization.Get(LocalizationKeys.SettingsNotChecked)
                : _settings.BrokerStateText),
            Item("DiagnosticsServiceConnection", _settings.BrokerDetailText)
        ]);

        Replace(StorageItems,
        [
            Item("DiagnosticsHistoryEnabled", _localization.Get("DiagnosticsEnabled")),
            Item("DiagnosticsDatabasePath", _history.DatabasePath, technical: true, includeInSafeSummary: false),
            Item("DiagnosticsDatabaseSize", FormatBytes(history.DatabaseBytes), technical: true),
            Item("DiagnosticsRetention", _localization.Format("DiagnosticsHours", _history.Retention.TotalHours)),
            Item("DiagnosticsStorageQueue", $"{history.QueueDepth}/{_history.QueueCapacity}", technical: true),
            Item("DiagnosticsStorageWrites", $"{history.BatchesWritten} / {history.SamplesWritten}", technical: true),
            Item("DiagnosticsStorageDrops", history.QueueDrops.ToString(CultureInfo.InvariantCulture), technical: true),
            Item("DiagnosticsStorageFailures", history.WriteFailures.ToString(CultureInfo.InvariantCulture), technical: true),
            Item("DiagnosticsStorageRecovery", history.LastError?.Contains("corrupt", StringComparison.OrdinalIgnoreCase) == true
                ? _localization.Get("DiagnosticsRecoveredCorruption")
                : _localization.Get("DiagnosticsNoRecovery"))
        ]);

        PhysicalDiskCollectorDiagnostics diskDiagnostics = applications.FirstOrDefault()?.PhysicalDisk.Diagnostics ?? default;
        NetworkCollectorDiagnostics networkDiagnostics = applications.FirstOrDefault()?.Network.Diagnostics ?? default;
        GpuCollectorDiagnostics gpuDiagnostics = applications.FirstOrDefault()?.Gpu.Diagnostics ?? default;
        Replace(AdvancedItems,
        [
            Item("DiagnosticsServiceIdentity", "MonitoringXS.PrivilegedEtwBroker", technical: true),
            Item("DiagnosticsProtocol", "v1", technical: true),
            Item("DiagnosticsDiskProvider", "Privileged Metrics Service / kernel disk provider (ETW)", technical: true),
            Item("DiagnosticsDiskQueue", $"{diskDiagnostics.CurrentQueueDepth}/{diskDiagnostics.MaximumQueueDepth}", technical: true),
            Item("DiagnosticsDiskLost", $"{diskDiagnostics.EtwEventsLost + diskDiagnostics.QueueEventsDropped}", technical: true),
            Item("DiagnosticsDiskCompleteness", Completeness(
                applications.Length > 0,
                diskDiagnostics.SessionTotalsAreLowerBounds)),
            Item("DiagnosticsDiskDuration", $"{diskDiagnostics.ProcessingLatencyMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms", technical: true),
            Item("DiagnosticsDiskLastSuccess", FormatTimestamp(diskDiagnostics.LastSuccessfulEventTimestampUtc), technical: true),
            Item("DiagnosticsNetworkProvider", "Privileged Metrics Service / kernel network provider (ETW)", technical: true),
            Item("DiagnosticsNetworkQueue", $"{networkDiagnostics.CurrentQueueDepth}/{networkDiagnostics.QueueCapacity}", technical: true),
            Item("DiagnosticsNetworkLost", $"{networkDiagnostics.EtwEventsLost + networkDiagnostics.QueueEventsDropped}", technical: true),
            Item("DiagnosticsNetworkCompleteness", Completeness(
                applications.Length > 0,
                networkDiagnostics.SessionTotalsAreLowerBounds)),
            Item("DiagnosticsNetworkDuration", $"{networkDiagnostics.AverageProcessingLatencyMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} / {networkDiagnostics.MaximumProcessingLatencyMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms", technical: true),
            Item("DiagnosticsNetworkLastSuccess", FormatTimestamp(networkDiagnostics.LastSuccessfulEventTimestampUtc), technical: true),
            Item("DiagnosticsGpuProvider", string.IsNullOrWhiteSpace(gpuDiagnostics.ProviderName)
                ? GpuCollectorDiagnostics.WindowsPdhProvider
                : gpuDiagnostics.ProviderName, technical: true),
            Item("DiagnosticsGpuDuration", $"{gpuDiagnostics.CollectionDurationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms", technical: true),
            Item("DiagnosticsGpuPartial", gpuDiagnostics.CollectorStatus == MetricAvailability.Partial
                ? _localization.Get(LocalizationKeys.PartialLowerBound)
                : AvailabilityText(gpuDiagnostics.CollectorStatus))
        ]);

        CopyStatusText = string.Empty;
    }

    private DiagnosticItem CollectorItem(
        string labelKey,
        MetricAvailability availability,
        MetricDescriptionId metric) => Item(
            labelKey,
            AvailabilityText(availability),
            _metricExplanations.Reason(metric, availability));

    private DiagnosticItem Item(
        string labelKey,
        string value,
        string? detail = null,
        bool technical = false,
        bool includeInSafeSummary = true) => new(
            _localization.Get(labelKey),
            value,
            detail,
            technical,
            includeInSafeSummary)
        {
            ValueFlowDirection = technical || _localization.Direction == TextDirection.LeftToRight
                ? FlowDirection.LeftToRight
                : FlowDirection.RightToLeft
        };

    private string AvailabilityText(MetricAvailability availability) => availability switch
    {
        MetricAvailability.Available => _localization.Get(LocalizationKeys.Available),
        MetricAvailability.Partial => _localization.Get(LocalizationKeys.PartialLowerBound),
        MetricAvailability.WarmingUp => _localization.Get(LocalizationKeys.WarmingUp),
        MetricAvailability.AccessDenied => _localization.Get(LocalizationKeys.AccessDenied),
        MetricAvailability.Unsupported => _localization.Get(LocalizationKeys.Unsupported),
        MetricAvailability.Error => _localization.Get(LocalizationKeys.Error),
        _ => _localization.Get(LocalizationKeys.Unavailable)
    };

    private string Completeness(bool hasSnapshot, bool lowerBound) => !hasSnapshot
        ? _localization.Get(LocalizationKeys.Unavailable)
        : lowerBound
            ? _localization.Get(LocalizationKeys.PartialLowerBound)
            : _localization.Get("DiagnosticsComplete");

    private static MetricAvailability Combine(
        MetricAvailability first,
        MetricAvailability second) => Aggregate([first, second]);

    internal static MetricAvailability Aggregate(IEnumerable<MetricAvailability> states)
    {
        MetricAvailability[] values = states.ToArray();
        if (values.Length == 0)
        {
            return MetricAvailability.WarmingUp;
        }

        if (values.All(state => state == MetricAvailability.Available))
        {
            return MetricAvailability.Available;
        }

        if (values.Any(state => state is MetricAvailability.Available or MetricAvailability.Partial))
        {
            return MetricAvailability.Partial;
        }

        MetricAvailability[] priority =
        [
            MetricAvailability.Error,
            MetricAvailability.AccessDenied,
            MetricAvailability.Unsupported,
            MetricAvailability.Unavailable,
            MetricAvailability.WarmingUp
        ];
        return priority.First(values.Contains);
    }

    private string FormatDuration(TimeSpan value) =>
        _localization.Format("DiagnosticsDuration", (int)value.TotalHours, value.Minutes, value.Seconds);

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    private string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToString("O", CultureInfo.InvariantCulture)
        ?? _localization.Get(LocalizationKeys.Unavailable);

    private static string DisplayVersion()
    {
        Assembly assembly = typeof(DiagnosticsPageViewModel).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unavailable";
    }

    private static string BuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static void Replace(
        ObservableCollection<DiagnosticItem> target,
        IEnumerable<DiagnosticItem> items)
    {
        target.Clear();
        foreach (DiagnosticItem item in items)
        {
            target.Add(item);
        }
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs args) => Rebuild();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.LanguageChanged -= Localization_LanguageChanged;
        RefreshCommand.Cancel();
        CopySafeSummaryCommand.Cancel();
    }
}
