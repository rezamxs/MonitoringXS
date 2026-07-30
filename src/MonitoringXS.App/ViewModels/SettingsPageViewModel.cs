using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Broker;

namespace MonitoringXS.App.ViewModels;

public sealed record SettingsOption<T>(T Value, string Label, string Description);

public enum SettingsPageState
{
    Loading,
    Ready,
    Saving,
    Saved,
    ValidationError,
    StorageUnavailable
}

public enum BrokerOperationalState
{
    NotInstalled,
    Stopped,
    Running,
    BinaryMissing,
    ProtocolMismatch,
    ConnectionUnavailable,
    Healthy
}

public sealed record BrokerOperationalStatus(
    BrokerOperationalState State,
    string Label,
    string Detail);

internal interface IBrokerSettingsStatusProvider
{
    ValueTask<BrokerOperationalStatus> QueryAsync(CancellationToken cancellationToken);
}

internal sealed class BrokerSettingsStatusProvider(PrivilegedEtwBrokerClient client)
    : IBrokerSettingsStatusProvider
{
    public async ValueTask<BrokerOperationalStatus> QueryAsync(
        CancellationToken cancellationToken)
    {
        BrokerServiceSnapshot service = await BrokerServiceProbe.QueryAsync(cancellationToken);
        if (service.State == BrokerServiceState.NotInstalled)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.NotInstalled);
        }

        if (service.BinaryPresent == false)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.BinaryMissing);
        }

        if (service.State == BrokerServiceState.Stopped)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.Stopped);
        }

        if (service.State != BrokerServiceState.Running)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.ConnectionUnavailable);
        }

        NetworkEventBatch network = await client.ReadNetworkBatchAsync([], cancellationToken);
        if (network.Availability == MetricAvailability.Unsupported)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.ProtocolMismatch);
        }

        return network.Availability is MetricAvailability.Available
            or MetricAvailability.Partial
            or MetricAvailability.WarmingUp
            ? BrokerStatusPresentation.Create(BrokerOperationalState.Healthy)
            : BrokerStatusPresentation.Create(BrokerOperationalState.ConnectionUnavailable);
    }
}

internal static class BrokerStatusPresentation
{
    public static BrokerOperationalStatus Create(BrokerOperationalState state) => state switch
    {
        BrokerOperationalState.NotInstalled => new(
            state,
            "Not installed",
            "Network and Physical Disk metrics require the Privileged Broker."),
        BrokerOperationalState.Stopped => new(
            state,
            "Stopped",
            "The installed Privileged Broker service is stopped."),
        BrokerOperationalState.Running => new(
            state,
            "Running",
            "The service is running; connection health has not been confirmed."),
        BrokerOperationalState.BinaryMissing => new(
            state,
            "Binary missing",
            "The service is registered, but its Broker binary is unavailable."),
        BrokerOperationalState.ProtocolMismatch => new(
            state,
            "Protocol mismatch",
            "The app and Broker protocol versions do not match."),
        BrokerOperationalState.ConnectionUnavailable => new(
            state,
            "Connection unavailable",
            "The app could not establish a healthy local Broker connection."),
        _ => new(
            state,
            "Healthy",
            "Network and Physical Disk privileged metrics are available.")
    };
}

#pragma warning disable CA1001 // Save serialization gate lives for the app lifetime.
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(100);
    private readonly IApplicationSettingsStore _store;
    private readonly IMetricHistoryRetentionController _retention;
    private readonly IBrokerSettingsStatusProvider _broker;
    private readonly LiveRefreshCadence _cadence;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _settingsGate = new();
    private ApplicationSettings _settings = ApplicationSettings.Default;
    private int _saveVersion;

    public SettingsPageViewModel(
        IApplicationSettingsStore store,
        IMetricHistoryRetentionController retention,
        PrivilegedEtwBrokerClient brokerClient,
        LiveRefreshCadence cadence)
        : this(store, retention, new BrokerSettingsStatusProvider(brokerClient), cadence)
    {
    }

    internal SettingsPageViewModel(
        IApplicationSettingsStore store,
        IMetricHistoryRetentionController retention,
        IBrokerSettingsStatusProvider broker,
        LiveRefreshCadence cadence)
    {
        _store = store;
        _retention = retention;
        _broker = broker;
        _cadence = cadence;
        RefreshBrokerStatusCommand = new AsyncRelayCommand(RefreshBrokerStatusAsync);
    }

    public event Action<ApplicationTheme>? ThemeRequested;

    public IAsyncRelayCommand RefreshBrokerStatusCommand { get; }

    public IReadOnlyList<SettingsOption<int>> SamplingOptions { get; } =
    [
        new(1, "1 second", "Most responsive; highest sampling overhead."),
        new(2, "2 seconds", "Balanced lower-frequency sampling."),
        new(5, "5 seconds", "Lowest sampling overhead.")
    ];

    public IReadOnlyList<SettingsOption<int>> RetentionOptions { get; } =
    [
        new(6, "6 hours", "Older history is removed during asynchronous maintenance."),
        new(24, "24 hours", "Default retention."),
        new(72, "3 days", "Keeps three days of bounded local history."),
        new(168, "7 days", "Keeps seven days within the database-size limit.")
    ];

    public IReadOnlyList<SettingsOption<ApplicationTheme>> ThemeOptions { get; } =
    [
        new(ApplicationTheme.System, "System", "Follow Windows theme."),
        new(ApplicationTheme.Light, "Light", "Use the light application theme."),
        new(ApplicationTheme.Dark, "Dark", "Use the dark application theme.")
    ];

    [ObservableProperty]
    public partial SettingsOption<int>? SelectedSampling { get; set; }

    [ObservableProperty]
    public partial SettingsOption<int>? SelectedRetention { get; set; }

    [ObservableProperty]
    public partial SettingsOption<ApplicationTheme>? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial SettingsPageState State { get; set; } = SettingsPageState.Loading;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading settings…";

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial bool IsBrokerRefreshing { get; set; }

    [ObservableProperty]
    public partial string BrokerStateText { get; set; } = "Not checked";

    [ObservableProperty]
    public partial string BrokerDetailText { get; set; } =
        "Refresh to check privileged metric availability.";

    public string AccessibilitySummary =>
        $"Sampling {SelectedSampling?.Label ?? "not loaded"}; "
        + $"history retention {SelectedRetention?.Label ?? "not loaded"}; "
        + $"theme {SelectedTheme?.Label ?? "not loaded"}; "
        + $"Privileged Broker {BrokerStateText}.";

    public void Initialize(ApplicationSettingsLoadResult load)
    {
        _settings = load.Settings.IsValid ? load.Settings : ApplicationSettings.Default;
        SelectedSampling = SamplingOptions.Single(option =>
            option.Value == _settings.LiveSamplingSeconds);
        SelectedRetention = RetentionOptions.Single(option =>
            option.Value == _settings.HistoryRetentionHours);
        SelectedTheme = ThemeOptions.Single(option => option.Value == _settings.Theme);
        _cadence.Update(_settings.LiveSamplingInterval);
        State = load.IsAvailable ? SettingsPageState.Ready : SettingsPageState.StorageUnavailable;
        StatusText = load.Recovered
            ? load.Error ?? "Settings recovered with safe defaults."
            : load.IsAvailable
                ? "Settings loaded."
                : load.Error ?? "Settings storage is unavailable.";
        OnPropertyChanged(nameof(AccessibilitySummary));
    }

    public Task SetSamplingAsync(
        SettingsOption<int> option,
        CancellationToken cancellationToken)
    {
        SelectedSampling = option;
        return ChangeAsync(
            settings => settings with { LiveSamplingSeconds = option.Value },
            settings =>
            {
                _cadence.Update(settings.LiveSamplingInterval);
                return ValueTask.FromResult(MetricHistoryRetentionResult.Success);
            },
            cancellationToken);
    }

    public Task SetRetentionAsync(
        SettingsOption<int> option,
        CancellationToken cancellationToken)
    {
        SelectedRetention = option;
        return ChangeAsync(
            settings => settings with { HistoryRetentionHours = option.Value },
            settings => _retention.UpdateRetentionAsync(
                settings.HistoryRetention,
                cancellationToken),
            cancellationToken);
    }

    public Task SetThemeAsync(
        SettingsOption<ApplicationTheme> option,
        CancellationToken cancellationToken)
    {
        SelectedTheme = option;
        return ChangeAsync(
            settings => settings with { Theme = option.Value },
            settings =>
            {
                ThemeRequested?.Invoke(settings.Theme);
                return ValueTask.FromResult(MetricHistoryRetentionResult.Success);
            },
            cancellationToken);
    }

    public async Task RefreshBrokerStatusAsync(CancellationToken cancellationToken)
    {
        IsBrokerRefreshing = true;
        BrokerStateText = "Checking…";
        try
        {
            BrokerOperationalStatus status = await _broker.QueryAsync(cancellationToken);
            BrokerStateText = status.Label;
            BrokerDetailText = status.Detail;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            BrokerStateText = "Connection unavailable";
            BrokerDetailText = "Broker status could not be checked.";
        }
        finally
        {
            IsBrokerRefreshing = false;
            OnPropertyChanged(nameof(AccessibilitySummary));
        }
    }

    private async Task ChangeAsync(
        Func<ApplicationSettings, ApplicationSettings> update,
        Func<ApplicationSettings, ValueTask<MetricHistoryRetentionResult>> apply,
        CancellationToken cancellationToken)
    {
        int version = 0;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            ApplicationSettings settings;
            lock (_settingsGate)
            {
                settings = update(_settings);
            }

            if (!settings.IsValid)
            {
                State = SettingsPageState.ValidationError;
                StatusText = "The selected setting is not supported.";
                return;
            }

            MetricHistoryRetentionResult applied = await apply(settings);
            if (!applied.Succeeded)
            {
                State = SettingsPageState.ValidationError;
                StatusText = applied.Error ?? "The setting could not be applied.";
                return;
            }

            lock (_settingsGate)
            {
                _settings = settings;
            }
            version = Interlocked.Increment(ref _saveVersion);
            IsSaving = true;
            State = SettingsPageState.Saving;
            StatusText = "Saving settings…";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            State = SettingsPageState.StorageUnavailable;
            StatusText = "Settings storage is unavailable.";
        }
        finally
        {
            _saveGate.Release();
        }

        if (version == 0)
        {
            IsSaving = false;
            return;
        }

        try
        {
            await Task.Delay(SaveDebounce, cancellationToken);
            if (version != Volatile.Read(ref _saveVersion))
            {
                return;
            }

            await _saveGate.WaitAsync(cancellationToken);
            try
            {
                ApplicationSettings settings;
                lock (_settingsGate)
                {
                    settings = _settings;
                }

                ApplicationSettingsSaveResult saved = await _store.SaveAsync(
                    settings,
                    cancellationToken);
                if (version != Volatile.Read(ref _saveVersion))
                {
                    return;
                }

                State = saved.Succeeded
                    ? SettingsPageState.Saved
                    : SettingsPageState.StorageUnavailable;
                StatusText = saved.Succeeded
                    ? "Settings saved."
                    : saved.Error ?? "Settings storage is unavailable.";
                OnPropertyChanged(nameof(AccessibilitySummary));
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (version == Volatile.Read(ref _saveVersion))
            {
                State = SettingsPageState.StorageUnavailable;
                StatusText = "Settings storage is unavailable.";
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _saveVersion))
            {
                IsSaving = false;
            }
        }
    }
}
#pragma warning restore CA1001
