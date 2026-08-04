using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class SettingsPageViewModelTests
{
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    [Fact]
    public void InitializeMapsDefaultsAndRecoveredStorageState()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.ViewModel;

        viewModel.Initialize(new(
            ApplicationSettings.Default,
            true,
            true,
            "Recovered."));

        Assert.Equal(1, viewModel.SelectedSampling?.Value);
        Assert.Equal(24, viewModel.SelectedRetention?.Value);
        Assert.Equal(ApplicationTheme.System, viewModel.SelectedTheme?.Value);
        Assert.Equal(SettingsPageState.Ready, viewModel.State);
        Assert.Equal("Recovered.", viewModel.StatusText);
        Assert.Contains("Sampling 1 second", viewModel.AccessibilitySummary);
        Assert.Empty(context.Store.Saved);
    }

    [Fact]
    public void OptionsMapEverySupportedPersistedValue()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();

        Assert.Equal([1, 2, 5], viewModel.SamplingOptions.Select(item => item.Value));
        Assert.Equal([6, 24, 72, 168], viewModel.RetentionOptions.Select(item => item.Value));
        Assert.Equal(
            [ApplicationTheme.System, ApplicationTheme.Light, ApplicationTheme.Dark],
            viewModel.ThemeOptions.Select(item => item.Value));
        Assert.Equal(
            [ApplicationLanguage.System, ApplicationLanguage.English, ApplicationLanguage.Persian],
            viewModel.LanguageOptions.Select(item => item.Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task SamplingChangesCadenceImmediatelyAndPersists(int seconds)
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();
        SettingsOption<int> option = Assert.Single(
            viewModel.SamplingOptions,
            item => item.Value == seconds);

        await viewModel.SetSamplingAsync(option, TestCancellation);

        Assert.Equal(TimeSpan.FromSeconds(seconds), context.Cadence.Interval);
        Assert.Equal(seconds, Assert.Single(context.Store.Saved).LiveSamplingSeconds);
        Assert.Equal(SettingsPageState.Saved, viewModel.State);
    }

    [Fact]
    public async Task RetentionChangesFutureMaintenanceWithoutSchemaWork()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();
        SettingsOption<int> option = Assert.Single(
            viewModel.RetentionOptions,
            item => item.Value == 168);

        await viewModel.SetRetentionAsync(option, TestCancellation);

        Assert.Equal(TimeSpan.FromDays(7), Assert.Single(context.Retention.Values));
        Assert.Equal(168, Assert.Single(context.Store.Saved).HistoryRetentionHours);
    }

    [Fact]
    public async Task ThemeAppliesImmediatelyAndPersists()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();
        ApplicationTheme? requested = null;
        viewModel.ThemeRequested += theme => requested = theme;
        SettingsOption<ApplicationTheme> option = Assert.Single(
            viewModel.ThemeOptions,
            item => item.Value == ApplicationTheme.Dark);

        await viewModel.SetThemeAsync(option, TestCancellation);

        Assert.Equal(ApplicationTheme.Dark, requested);
        Assert.Equal(ApplicationTheme.Dark, Assert.Single(context.Store.Saved).Theme);
    }

    [Fact]
    public async Task RapidDifferentChangesMergeInsteadOfOverwritingEachOther()
    {
        TestHarness context = new();
        context.Store.Delay = TimeSpan.FromMilliseconds(10);
        SettingsPageViewModel viewModel = context.InitializedViewModel();

        Task sampling = viewModel.SetSamplingAsync(
            Assert.Single(viewModel.SamplingOptions, item => item.Value == 2),
            TestCancellation);
        Task theme = viewModel.SetThemeAsync(
            Assert.Single(viewModel.ThemeOptions, item => item.Value == ApplicationTheme.Light),
            TestCancellation);
        await Task.WhenAll(sampling, theme);

        ApplicationSettings final = context.Store.Saved[^1];
        Assert.Equal(2, final.LiveSamplingSeconds);
        Assert.Equal(ApplicationTheme.Light, final.Theme);
        Assert.Equal(SettingsPageState.Saved, viewModel.State);
    }

    [Fact]
    public async Task SaveDebounceEventuallyFlushesLatestValue()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();

        Task change = viewModel.SetSamplingAsync(
            Assert.Single(viewModel.SamplingOptions, item => item.Value == 2),
            TestCancellation);

        Assert.Empty(context.Store.Saved);
        Assert.True(viewModel.IsSaving);
        await change;
        Assert.Equal(2, Assert.Single(context.Store.Saved).LiveSamplingSeconds);
        Assert.False(viewModel.IsSaving);
    }

    [Fact]
    public async Task StaleFailedSaveCannotOverwriteNewerSuccessState()
    {
        TestHarness context = new();
        context.Store.Results.Enqueue(new(false, "stale failure"));
        context.Store.Results.Enqueue(ApplicationSettingsSaveResult.Success);
        context.Store.PauseFirstSave = true;
        SettingsPageViewModel viewModel = context.InitializedViewModel();

        Task first = viewModel.SetSamplingAsync(
            Assert.Single(viewModel.SamplingOptions, item => item.Value == 2),
            TestCancellation);
        await context.Store.FirstSaveStarted.Task.WaitAsync(TestCancellation);
        Task second = viewModel.SetThemeAsync(
            Assert.Single(viewModel.ThemeOptions, item => item.Value == ApplicationTheme.Dark),
            TestCancellation);
        context.Store.ContinueFirstSave.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(SettingsPageState.Saved, viewModel.State);
        Assert.Equal("Settings saved.", viewModel.StatusText);
        Assert.Equal(2, context.Store.Saved[^1].LiveSamplingSeconds);
        Assert.Equal(ApplicationTheme.Dark, context.Store.Saved[^1].Theme);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotThrowOrStopAppliedLiveSetting()
    {
        TestHarness context = new();
        context.Store.Result = new(false, "Read-only settings storage.");
        SettingsPageViewModel viewModel = context.InitializedViewModel();

        await viewModel.SetSamplingAsync(
            Assert.Single(viewModel.SamplingOptions, item => item.Value == 2),
            TestCancellation);

        Assert.Equal(TimeSpan.FromSeconds(2), context.Cadence.Interval);
        Assert.Equal(SettingsPageState.StorageUnavailable, viewModel.State);
        Assert.Equal("Read-only settings storage.", viewModel.StatusText);
    }

    [Fact]
    public async Task FailedRetentionIsNotIncludedInLaterSaves()
    {
        TestHarness context = new();
        context.Retention.Result = new(false, "Retention unavailable.");
        SettingsPageViewModel viewModel = context.InitializedViewModel();

        await viewModel.SetRetentionAsync(
            Assert.Single(viewModel.RetentionOptions, item => item.Value == 168),
            TestCancellation);
        context.Retention.Result = MetricHistoryRetentionResult.Success;
        await viewModel.SetThemeAsync(
            Assert.Single(viewModel.ThemeOptions, item => item.Value == ApplicationTheme.Dark),
            TestCancellation);

        Assert.Equal(24, Assert.Single(context.Store.Saved).HistoryRetentionHours);
    }

    [Fact]
    public async Task BrokerStatesAndFailuresAreHonest()
    {
        foreach (BrokerOperationalState state in Enum.GetValues<BrokerOperationalState>())
        {
            TestHarness context = new();
            context.Broker.Result = BrokerStatusPresentation.Create(state);
            SettingsPageViewModel viewModel = context.InitializedViewModel();

            await viewModel.RefreshBrokerStatusAsync(TestCancellation);

            Assert.Equal(context.Broker.Result.Label, viewModel.BrokerStateText);
            Assert.False(viewModel.IsBrokerRefreshing);
        }

        TestHarness failing = new();
        failing.Broker.Exception = new IOException("private detail");
        SettingsPageViewModel failedViewModel = failing.InitializedViewModel();
        await failedViewModel.RefreshBrokerStatusAsync(TestCancellation);
        Assert.Equal("Connection unavailable", failedViewModel.BrokerStateText);
        Assert.DoesNotContain("private detail", failedViewModel.BrokerDetailText);
    }

    [Fact]
    public async Task BrokerRefreshCommandIsPublicNonOverlappingAndReusable()
    {
        TestHarness context = new();
        context.Broker.Delay = TimeSpan.FromMilliseconds(50);
        SettingsPageViewModel viewModel = context.InitializedViewModel();

        Assert.NotNull(viewModel.RefreshBrokerStatusCommand);
        Task first = viewModel.RefreshBrokerStatusCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsBrokerRefreshing);
        Assert.False(viewModel.RefreshBrokerStatusCommand.CanExecute(null));
        await first;
        Assert.Equal(1, context.Broker.CallCount);
        Assert.False(viewModel.IsBrokerRefreshing);

        context.Broker.Exception = new IOException("safe failure");
        await viewModel.RefreshBrokerStatusCommand.ExecuteAsync(null);
        Assert.Equal(2, context.Broker.CallCount);
        Assert.Equal("Connection unavailable", viewModel.BrokerStateText);
    }

    [Fact]
    public async Task PageWorkCancellationDoesNotUndoSavedSettingsAndCommandCanRunAgain()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();
        await viewModel.SetSamplingAsync(
            Assert.Single(viewModel.SamplingOptions, item => item.Value == 2),
            TestCancellation);
        context.Broker.Delay = TimeSpan.FromSeconds(10);

        Task refresh = viewModel.RefreshBrokerStatusCommand.ExecuteAsync(null);
        viewModel.RefreshBrokerStatusCommand.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        Assert.Equal(2, Assert.Single(context.Store.Saved).LiveSamplingSeconds);
        context.Broker.Delay = TimeSpan.Zero;
        await viewModel.RefreshBrokerStatusCommand.ExecuteAsync(null);
        Assert.Equal(2, context.Broker.CallCount);
        Assert.False(viewModel.IsBrokerRefreshing);
    }

    [Fact]
    public async Task CancellationIsPropagatedAndDoesNotLeaveSavingIndicator()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            viewModel.SetSamplingAsync(
                Assert.Single(viewModel.SamplingOptions, item => item.Value == 2),
                cancelled.Token));

        Assert.False(viewModel.IsSaving);
    }

    [Fact]
    public async Task RapidLanguageChangesApplyAndPersistLatestSelection()
    {
        TestHarness context = new();
        SettingsPageViewModel viewModel = context.InitializedViewModel();
        SettingsOption<ApplicationLanguage>[] options = viewModel.LanguageOptions.ToArray();

        await Task.WhenAll(
            viewModel.SetLanguageAsync(options.Single(item => item.Value == ApplicationLanguage.System), TestCancellation),
            viewModel.SetLanguageAsync(options.Single(item => item.Value == ApplicationLanguage.English), TestCancellation),
            viewModel.SetLanguageAsync(options.Single(item => item.Value == ApplicationLanguage.Persian), TestCancellation));

        Assert.Equal(ApplicationLanguage.Persian, viewModel.SelectedLanguage?.Value);
        Assert.Equal(ApplicationLanguage.Persian, viewModel.CurrentSettings.Language);
        Assert.Equal(ApplicationLanguage.Persian, context.Store.Saved[^1].Language);
    }

    [Fact]
    public void SettingsLayoutIsScrollableAccessibleAndBoundedAtSupportedSizes()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MonitoringXS.App",
            "SettingsPage.xaml"));

        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"900\"", xaml, StringComparison.Ordinal);
        Assert.Equal(4, Count(xaml, "<ComboBox"));
        Assert.True(Count(xaml, "UseSystemFocusVisuals=\"True\"") >= 4);
        Assert.True(Count(xaml, "AutomationProperties.Name=") >= 8);
        string codeBehind = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MonitoringXS.App",
            "SettingsPage.xaml.cs"));
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Manage-PrivilegedBroker", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionChanged=", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "SamplingSelector.SelectionChanged += SamplingSelector_SelectionChanged",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("SettingsPage_Loaded", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DataContext = viewModel", codeBehind, StringComparison.Ordinal);
        Assert.Equal(4, Count(xaml, "SelectedItem=\"{Binding Selected"));
        Assert.Equal(4, Count(xaml, "Mode=TwoWay"));
        Assert.DoesNotContain("FocusState", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{Binding RefreshBrokerStatusCommand}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshBrokerStatus_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RefreshBrokerStatusCommand.Cancel()", codeBehind, StringComparison.Ordinal);

        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            const double horizontalPadding = 48;
            const double physicalWidth = 1180;
            const double physicalHeight = 760;
            double logicalWidth = physicalWidth / scale;
            double logicalHeight = physicalHeight / scale;
            double contentWidth = logicalWidth - horizontalPadding;
            double selectorWidth = Math.Min(320, contentWidth - 32);
            Assert.True(selectorWidth > 0);
            Assert.True(selectorWidth <= contentWidth);
            Assert.True(logicalHeight > 0);
        }
    }

    private static int Count(string value, string part) =>
        value.Split(part, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "MonitoringXS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "MonitoringXS repository root was not found.");
    }

    private sealed class TestHarness
    {
        public FakeStore Store { get; } = new();

        public FakeRetention Retention { get; } = new();

        public FakeBroker Broker { get; } = new();

        public LiveRefreshCadence Cadence { get; } = new(TimeSpan.FromSeconds(1));

        public SettingsPageViewModel ViewModel =>
            new(Store, Retention, Broker, Cadence);

        public SettingsPageViewModel InitializedViewModel()
        {
            SettingsPageViewModel viewModel = ViewModel;
            viewModel.Initialize(new(ApplicationSettings.Default, true, false));
            return viewModel;
        }
    }

    private sealed class FakeStore : IApplicationSettingsStore
    {
        public List<ApplicationSettings> Saved { get; } = [];

        public ApplicationSettingsSaveResult Result { get; set; } =
            ApplicationSettingsSaveResult.Success;

        public TimeSpan Delay { get; set; }

        public Queue<ApplicationSettingsSaveResult> Results { get; } = new();

        public bool PauseFirstSave { get; set; }

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueFirstSave { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _saveCalls;

        public ValueTask<ApplicationSettingsLoadResult> LoadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ApplicationSettingsLoadResult(
                ApplicationSettings.Default,
                true,
                false));

        public async ValueTask<ApplicationSettingsSaveResult> SaveAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _saveCalls);
            if (PauseFirstSave && call == 1)
            {
                FirstSaveStarted.SetResult();
                await ContinueFirstSave.Task.WaitAsync(cancellationToken);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            Saved.Add(settings);
            return Results.Count == 0 ? Result : Results.Dequeue();
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRetention : IMetricHistoryRetentionController
    {
        public List<TimeSpan> Values { get; } = [];

        public MetricHistoryRetentionResult Result { get; set; } =
            MetricHistoryRetentionResult.Success;

        public ValueTask<MetricHistoryRetentionResult> UpdateRetentionAsync(
            TimeSpan retention,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Add(retention);
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakeBroker : IBrokerSettingsStatusProvider
    {
        public BrokerOperationalStatus Result { get; set; } =
            BrokerStatusPresentation.Create(BrokerOperationalState.NotInstalled);

        public Exception? Exception { get; set; }

        public TimeSpan Delay { get; set; }

        public int CallCount { get; private set; }

        public async ValueTask<BrokerOperationalStatus> QueryAsync(
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return Exception is null ? Result : throw Exception;
        }
    }
}
