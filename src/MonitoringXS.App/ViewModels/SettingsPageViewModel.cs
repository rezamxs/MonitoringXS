using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitoringXS.App.Localization;
using MonitoringXS.Application;
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

internal sealed class BrokerSettingsStatusProvider(
    PrivilegedEtwBrokerClient client,
    LocalizationService localization)
    : IBrokerSettingsStatusProvider
{
    public async ValueTask<BrokerOperationalStatus> QueryAsync(
        CancellationToken cancellationToken)
    {
        BrokerServiceSnapshot service = await BrokerServiceProbe.QueryAsync(cancellationToken);
        if (service.State == BrokerServiceState.NotInstalled)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.NotInstalled, localization);
        }

        if (service.BinaryPresent == false)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.BinaryMissing, localization);
        }

        if (service.State == BrokerServiceState.Stopped)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.Stopped, localization);
        }

        if (service.State != BrokerServiceState.Running)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.ConnectionUnavailable, localization);
        }

        NetworkEventBatch network = await client.ReadNetworkBatchAsync([], cancellationToken);
        if (network.Availability == MetricAvailability.Unsupported)
        {
            return BrokerStatusPresentation.Create(BrokerOperationalState.ProtocolMismatch, localization);
        }

        return network.Availability is MetricAvailability.Available
            or MetricAvailability.Partial
            or MetricAvailability.WarmingUp
            ? BrokerStatusPresentation.Create(BrokerOperationalState.Healthy, localization)
            : BrokerStatusPresentation.Create(BrokerOperationalState.ConnectionUnavailable, localization);
    }
}

internal static class BrokerStatusPresentation
{
    public static BrokerOperationalStatus Create(
        BrokerOperationalState state,
        LocalizationService? localization = null)
    {
        localization ??= new LocalizationService();
        return state switch
        {
        BrokerOperationalState.NotInstalled => new(
            state,
            localization.Get(LocalizationKeys.BrokerNotInstalledLabel),
            localization.Get(LocalizationKeys.BrokerNotInstalledDetail)),
        BrokerOperationalState.Stopped => new(
            state,
            localization.Get(LocalizationKeys.BrokerStoppedLabel),
            localization.Get(LocalizationKeys.BrokerStoppedDetail)),
        BrokerOperationalState.Running => new(
            state,
            localization.Get(LocalizationKeys.BrokerRunningLabel),
            localization.Get(LocalizationKeys.BrokerRunningDetail)),
        BrokerOperationalState.BinaryMissing => new(
            state,
            localization.Get(LocalizationKeys.BrokerBinaryMissingLabel),
            localization.Get(LocalizationKeys.BrokerBinaryMissingDetail)),
        BrokerOperationalState.ProtocolMismatch => new(
            state,
            localization.Get(LocalizationKeys.BrokerProtocolMismatchLabel),
            localization.Get(LocalizationKeys.BrokerProtocolMismatchDetail)),
        BrokerOperationalState.ConnectionUnavailable => new(
            state,
            localization.Get(LocalizationKeys.BrokerConnectionUnavailableLabel),
            localization.Get(LocalizationKeys.BrokerConnectionUnavailableDetail)),
        _ => new(
            state,
            localization.Get(LocalizationKeys.BrokerHealthyLabel),
            localization.Get(LocalizationKeys.BrokerHealthyDetail))
        };
    }
}

#pragma warning disable CA1001 // Save serialization gate lives for the app lifetime.
public sealed partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(100);
    private readonly IApplicationSettingsStore _store;
    private readonly IMetricHistoryRetentionController _retention;
    private readonly IBrokerSettingsStatusProvider _broker;
    private readonly LiveRefreshCadence _cadence;
    private readonly LocalizationService _localization;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _settingsGate = new();
    private ApplicationSettings _settings = ApplicationSettings.Default;
    private int _saveVersion;
    private BrokerOperationalState? _brokerState;

    public SettingsPageViewModel(
        IApplicationSettingsStore store,
        IMetricHistoryRetentionController retention,
        PrivilegedEtwBrokerClient brokerClient,
        LiveRefreshCadence cadence,
        LocalizationService? localization = null)
        : this(
            store,
            retention,
            new BrokerSettingsStatusProvider(brokerClient, localization ?? new LocalizationService()),
            cadence,
            localization)
    {
    }

    internal SettingsPageViewModel(
        IApplicationSettingsStore store,
        IMetricHistoryRetentionController retention,
        IBrokerSettingsStatusProvider broker,
        LiveRefreshCadence cadence,
        LocalizationService? localization = null)
    {
        _store = store;
        _retention = retention;
        _broker = broker;
        _cadence = cadence;
        _localization = localization ?? new LocalizationService();
        BuildOptions();
        _localization.LanguageChanged += Localization_LanguageChanged;
        RefreshBrokerStatusCommand = new AsyncRelayCommand(RefreshBrokerStatusAsync);
    }

    public event Action<ApplicationTheme>? ThemeRequested;

    public IAsyncRelayCommand RefreshBrokerStatusCommand { get; }

    public ApplicationSettings CurrentSettings
    {
        get
        {
            lock (_settingsGate)
            {
                return _settings;
            }
        }
    }

    public IReadOnlyList<SettingsOption<int>> SamplingOptions { get; private set; } = [];

    public IReadOnlyList<SettingsOption<int>> RetentionOptions { get; private set; } = [];

    public IReadOnlyList<SettingsOption<ApplicationTheme>> ThemeOptions { get; private set; } = [];

    public IReadOnlyList<SettingsOption<ApplicationLanguage>> LanguageOptions { get; private set; } = [];

    [ObservableProperty]
    public partial SettingsOption<int>? SelectedSampling { get; set; }

    [ObservableProperty]
    public partial SettingsOption<int>? SelectedRetention { get; set; }

    [ObservableProperty]
    public partial SettingsOption<ApplicationTheme>? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial SettingsOption<ApplicationLanguage>? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial SettingsPageState State { get; set; } = SettingsPageState.Loading;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial bool IsBrokerRefreshing { get; set; }

    [ObservableProperty]
    public partial string BrokerStateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BrokerDetailText { get; set; } = string.Empty;

    public string AccessibilitySummary => _localization.Format(
        LocalizationKeys.SettingsAccessibilitySummary,
        SelectedSampling?.Label ?? _localization.Get(LocalizationKeys.SettingsNotLoaded),
        SelectedRetention?.Label ?? _localization.Get(LocalizationKeys.SettingsNotLoaded),
        SelectedTheme?.Label ?? _localization.Get(LocalizationKeys.SettingsNotLoaded),
        SelectedLanguage?.Label ?? _localization.Get(LocalizationKeys.SettingsNotLoaded),
        BrokerStateText);

    public void Initialize(ApplicationSettingsLoadResult load)
    {
        BuildOptions();
        _settings = load.Settings.IsValid ? load.Settings : ApplicationSettings.Default;
        SelectedSampling = SamplingOptions.Single(option =>
            option.Value == _settings.LiveSamplingSeconds);
        SelectedRetention = RetentionOptions.Single(option =>
            option.Value == _settings.HistoryRetentionHours);
        SelectedTheme = ThemeOptions.Single(option => option.Value == _settings.Theme);
        SelectedLanguage = LanguageOptions.Single(option => option.Value == _settings.Language);
        _cadence.Update(_settings.LiveSamplingInterval);
        State = load.IsAvailable ? SettingsPageState.Ready : SettingsPageState.StorageUnavailable;
        StatusText = load.Recovered
            ? load.Error ?? _localization.Get(LocalizationKeys.SettingsRecovered)
            : load.IsAvailable
                ? _localization.Get(LocalizationKeys.SettingsLoaded)
                : load.Error ?? _localization.Get(LocalizationKeys.SettingsStorageUnavailable);
        BrokerStateText = _localization.Get(LocalizationKeys.SettingsNotChecked);
        BrokerDetailText = _localization.Get(LocalizationKeys.BrokerRefreshPrompt);
        OnPropertyChanged(nameof(AccessibilitySummary));
    }

    public async Task SetLanguageAsync(
        SettingsOption<ApplicationLanguage> option,
        CancellationToken cancellationToken)
    {
        SelectedLanguage = option;
        await ChangeAsync(
            settings => settings with { Language = option.Value },
            _ => ValueTask.FromResult(MetricHistoryRetentionResult.Success),
            cancellationToken);
        if (SelectedLanguage?.Value == option.Value)
        {
            _localization.SetLanguage(option.Value);
        }
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

    public BrokerOperationalState? BrokerState => _brokerState;

    internal Task SetApplicationSortAsync(
        ApplicationSortPreference field,
        bool descending,
        CancellationToken cancellationToken) => ChangeAsync(
            settings => settings with
            {
                ApplicationSort = field,
                ApplicationSortDescending = descending
            },
            _ => ValueTask.FromResult(MetricHistoryRetentionResult.Success),
            cancellationToken);

    public async Task RefreshBrokerStatusAsync(CancellationToken cancellationToken)
    {
        IsBrokerRefreshing = true;
        BrokerStateText = _localization.Get(LocalizationKeys.Checking);
        try
        {
            BrokerOperationalStatus status = await _broker.QueryAsync(cancellationToken);
            _brokerState = status.State;
            BrokerStateText = status.Label;
            BrokerDetailText = status.Detail;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _brokerState = BrokerOperationalState.ConnectionUnavailable;
            BrokerStateText = _localization.Get(LocalizationKeys.BrokerConnectionUnavailableLabel);
            BrokerDetailText = _localization.Get(LocalizationKeys.BrokerCheckFailed);
        }
        finally
        {
            IsBrokerRefreshing = false;
            OnPropertyChanged(nameof(AccessibilitySummary));
        }
    }

    private void BuildOptions()
    {
        int? sampling = SelectedSampling?.Value;
        int? retention = SelectedRetention?.Value;
        ApplicationTheme? theme = SelectedTheme?.Value;
        ApplicationLanguage? language = SelectedLanguage?.Value;
        SamplingOptions =
        [
            new(1, _localization.Get(LocalizationKeys.SamplingOneLabel), _localization.Get(LocalizationKeys.SamplingOneDescription)),
            new(2, _localization.Get(LocalizationKeys.SamplingTwoLabel), _localization.Get(LocalizationKeys.SamplingTwoDescription)),
            new(5, _localization.Get(LocalizationKeys.SamplingFiveLabel), _localization.Get(LocalizationKeys.SamplingFiveDescription))
        ];
        RetentionOptions =
        [
            new(6, _localization.Get(LocalizationKeys.RetentionSixLabel), _localization.Get(LocalizationKeys.RetentionSixDescription)),
            new(24, _localization.Get(LocalizationKeys.RetentionDayLabel), _localization.Get(LocalizationKeys.RetentionDayDescription)),
            new(72, _localization.Get(LocalizationKeys.RetentionThreeDaysLabel), _localization.Get(LocalizationKeys.RetentionThreeDaysDescription)),
            new(168, _localization.Get(LocalizationKeys.RetentionSevenDaysLabel), _localization.Get(LocalizationKeys.RetentionSevenDaysDescription))
        ];
        ThemeOptions =
        [
            new(ApplicationTheme.System, _localization.Get(LocalizationKeys.ThemeSystemLabel), _localization.Get(LocalizationKeys.ThemeSystemDescription)),
            new(ApplicationTheme.Light, _localization.Get(LocalizationKeys.ThemeLightLabel), _localization.Get(LocalizationKeys.ThemeLightDescription)),
            new(ApplicationTheme.Dark, _localization.Get(LocalizationKeys.ThemeDarkLabel), _localization.Get(LocalizationKeys.ThemeDarkDescription))
        ];
        LanguageOptions =
        [
            new(ApplicationLanguage.System, _localization.Get(LocalizationKeys.LanguageSystemLabel), _localization.Get(LocalizationKeys.LanguageSystemDescription)),
            new(ApplicationLanguage.English, _localization.Get(LocalizationKeys.LanguageEnglishLabel), _localization.Get(LocalizationKeys.LanguageEnglishDescription)),
            new(ApplicationLanguage.Persian, _localization.Get(LocalizationKeys.LanguagePersianLabel), _localization.Get(LocalizationKeys.LanguagePersianDescription))
        ];
        if (sampling is not null) SelectedSampling = SamplingOptions.Single(item => item.Value == sampling);
        if (retention is not null) SelectedRetention = RetentionOptions.Single(item => item.Value == retention);
        if (theme is not null) SelectedTheme = ThemeOptions.Single(item => item.Value == theme);
        if (language is not null) SelectedLanguage = LanguageOptions.Single(item => item.Value == language);
        OnPropertyChanged(nameof(AccessibilitySummary));
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs args)
    {
        BuildOptions();
        if (_brokerState is BrokerOperationalState state)
        {
            BrokerOperationalStatus status = BrokerStatusPresentation.Create(state, _localization);
            BrokerStateText = status.Label;
            BrokerDetailText = status.Detail;
        }

        OnPropertyChanged(nameof(AccessibilitySummary));
        StatusText = State switch
        {
            SettingsPageState.Loading => _localization.Get(LocalizationKeys.LoadingSettings),
            SettingsPageState.Ready => _localization.Get(LocalizationKeys.SettingsLoaded),
            SettingsPageState.Saving => _localization.Get(LocalizationKeys.SavingSettings),
            SettingsPageState.Saved => _localization.Get(LocalizationKeys.SettingsSaved),
            SettingsPageState.ValidationError => _localization.Get(LocalizationKeys.SelectedSettingUnsupported),
            SettingsPageState.StorageUnavailable => _localization.Get(LocalizationKeys.SettingsStorageUnavailable),
            _ => StatusText
        };
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
                StatusText = _localization.Get(LocalizationKeys.SelectedSettingUnsupported);
                return;
            }

            MetricHistoryRetentionResult applied = await apply(settings);
            if (!applied.Succeeded)
            {
                State = SettingsPageState.ValidationError;
                StatusText = _localization.Get(LocalizationKeys.SettingApplyFailed);
                return;
            }

            lock (_settingsGate)
            {
                _settings = settings;
            }
            version = Interlocked.Increment(ref _saveVersion);
            IsSaving = true;
            State = SettingsPageState.Saving;
            StatusText = _localization.Get(LocalizationKeys.SavingSettings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            State = SettingsPageState.StorageUnavailable;
            StatusText = _localization.Get(LocalizationKeys.SettingsStorageUnavailable);
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
                    ? _localization.Get(LocalizationKeys.SettingsSaved)
                    : saved.Error ?? _localization.Get(LocalizationKeys.SettingsStorageUnavailable);
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
                StatusText = _localization.Get(LocalizationKeys.SettingsStorageUnavailable);
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

    public void Dispose()
    {
        _localization.LanguageChanged -= Localization_LanguageChanged;
        RefreshBrokerStatusCommand.Cancel();
    }
}
#pragma warning restore CA1001
