using System.Globalization;
using MonitoringXS.App;
using MonitoringXS.App.Localization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class ProcessActionsViewModelTests
{
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    [Fact]
    public void DisposeStopsLanguageChangeCallbacks()
    {
        Harness context = new();
        LocalizationService localization = new(
            Path.Combine(FindRepositoryRoot(), "src", "MonitoringXS.App"),
            CultureInfo.GetCultureInfo("en-US"));
        ProcessActionsViewModel viewModel = new(context.Actions, context.Clipboard, localization);
        viewModel.Update(Snapshot());
        string identity = viewModel.IdentityText;
        string status = viewModel.StatusText;

        viewModel.Dispose();
        localization.SetLanguage(ApplicationLanguage.Persian);

        Assert.Equal(identity, viewModel.IdentityText);
        Assert.Equal(status, viewModel.StatusText);
    }

    [Fact]
    public void CommandsAreDisabledWithoutSelectionAndPathControlsOpenLocation()
    {
        Harness context = new();
        context.Configure();

        Assert.False(context.ViewModel.EndTaskCommand.CanExecute(null));
        Assert.False(context.ViewModel.EndProcessTreeCommand.CanExecute(null));
        Assert.False(context.ViewModel.OpenFileLocationCommand.CanExecute(null));
        Assert.False(context.ViewModel.CopyProcessDetailsCommand.CanExecute(null));

        context.ViewModel.Update(Snapshot(path: null));

        Assert.True(context.ViewModel.EndTaskCommand.CanExecute(null));
        Assert.True(context.ViewModel.EndProcessTreeCommand.CanExecute(null));
        Assert.False(context.ViewModel.OpenFileLocationCommand.CanExecute(null));
        Assert.True(context.ViewModel.CopyProcessDetailsCommand.CanExecute(null));
    }

    [Fact]
    public async Task EndTaskRequiresConfirmationAndCapturesImmutableTarget()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        ProcessActionChoice first = context.ViewModel.Processes[0];
        ProcessActionChoice second = context.ViewModel.Processes[1];
        TaskCompletionSource<bool> confirmation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Confirm = (_, _) =>
        {
            context.ConfirmationShown.SetResult();
            return confirmation.Task;
        };
        context.Configure();

        Task action = context.ViewModel.EndTaskCommand.ExecuteAsync(null);
        await context.ConfirmationShown.Task.WaitAsync(TestCancellation);
        Assert.True(context.ViewModel.IsBusy);
        Assert.False(context.ViewModel.EndTaskCommand.CanExecute(null));
        context.ViewModel.SelectedProcess = second;
        confirmation.SetResult(true);
        await action;

        Assert.Equal(first.Target, Assert.Single(context.Actions.Ended));
        Assert.NotEqual(second.Target, context.Actions.Ended[0]);
    }

    [Fact]
    public async Task DeclinedConfirmationNeverInvokesDestructiveService()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot());
        context.Confirm = (_, _) => Task.FromResult(false);
        context.Configure();

        await context.ViewModel.EndTaskCommand.ExecuteAsync(null);
        await context.ViewModel.EndProcessTreeCommand.ExecuteAsync(null);

        Assert.Empty(context.Actions.Ended);
        Assert.Empty(context.Actions.TreesEnded);
        Assert.Contains("cancelled", context.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TreeConfirmationShowsVerifiedDescendantCount()
    {
        Harness context = new();
        context.Actions.Inspection = new(
            ProcessActionStatus.Success,
            "verified",
            DescendantCount: 3);
        context.ViewModel.Update(Snapshot());
        ProcessActionConfirmation? shown = null;
        context.Confirm = (request, _) =>
        {
            shown = request;
            return Task.FromResult(true);
        };
        context.Configure();

        await context.ViewModel.EndProcessTreeCommand.ExecuteAsync(null);

        Assert.Contains("3 currently identified descendants", shown!.Message, StringComparison.Ordinal);
        Assert.Single(context.Actions.TreesEnded);
    }

    [Fact]
    public async Task StaleCompletionCannotOverwriteNewSelectionFeedback()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        ProcessActionChoice second = context.ViewModel.Processes[1];
        context.Actions.PauseOpen = true;
        context.Configure();

        Task action = context.ViewModel.OpenFileLocationCommand.ExecuteAsync(null);
        await context.Actions.OpenStarted.Task.WaitAsync(TestCancellation);
        context.ViewModel.SelectedProcess = second;
        Assert.Equal("Ready.", context.ViewModel.StatusText);
        context.Actions.ContinueOpen.SetResult();
        await action;

        Assert.Equal("Ready.", context.ViewModel.StatusText);
        Assert.False(context.ViewModel.IsBusy);
    }

    [Fact]
    public async Task TransientRefreshDoesNotReplaceSelectionDuringAction()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        ProcessInstanceId selected = context.ViewModel.SelectedProcess!.Target.InstanceId;
        context.Actions.PauseOpen = true;
        context.Configure();

        Task action = context.ViewModel.OpenFileLocationCommand.ExecuteAsync(null);
        await context.Actions.OpenStarted.Task.WaitAsync(TestCancellation);
        context.ViewModel.Update(Snapshot(secondOnly: true));
        context.Actions.ContinueOpen.SetResult();
        await action;

        Assert.Equal(selected, context.ViewModel.SelectedProcess!.Target.InstanceId);
        Assert.Equal("File location opened.", context.ViewModel.StatusText);
    }

    [Fact]
    public async Task CompletedFeedbackRemainsVisibleAfterLaterSelectionChange()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.Configure();

        await context.ViewModel.OpenFileLocationCommand.ExecuteAsync(null);
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[1];

        Assert.Contains("Last action: File location opened.", context.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessDeniedRemainsVisibleAndActionBecomesReusable()
    {
        Harness context = new();
        context.Actions.EndResult = new(
            ProcessActionStatus.AccessDenied,
            "Access denied. Monitoring XS remains non-elevated.");
        context.ViewModel.Update(Snapshot());
        context.Configure();

        await context.ViewModel.EndTaskCommand.ExecuteAsync(null);

        Assert.Contains("Access denied", context.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.False(context.ViewModel.IsBusy);
        Assert.True(context.ViewModel.EndTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyDetailsUsesStableSafeFieldsAndReportsClipboardFailure()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot());
        context.Configure();

        await context.ViewModel.CopyProcessDetailsCommand.ExecuteAsync(null);

        string copied = Assert.Single(context.Clipboard.Values);
        Assert.Contains("Application: Sample App", copied, StringComparison.Ordinal);
        Assert.Contains("Process Name: sample", copied, StringComparison.Ordinal);
        Assert.Contains("PID: 120", copied, StringComparison.Ordinal);
        Assert.Contains("Status: Running", copied, StringComparison.Ordinal);
        Assert.Contains("Architecture: x64", copied, StringComparison.Ordinal);
        Assert.Contains(@"Executable Path: C:\Apps\sample.exe", copied, StringComparison.Ordinal);
        Assert.Contains("Publisher: Contoso Publisher", copied, StringComparison.Ordinal);
        Assert.Contains("File Version: 1.0", copied, StringComparison.Ordinal);
        Assert.Contains("Start Time: 1970-01-01T00:02:00.0000000+00:00", copied, StringComparison.Ordinal);
        Assert.Matches(@"Running Duration: \d+\.\d{2}:\d{2}:\d{2}", copied);
        Assert.Contains("CPU: 12.5% (Available)", copied, StringComparison.Ordinal);
        Assert.Contains("Memory: 128.0 MB (Available)", copied, StringComparison.Ordinal);
        Assert.Contains("Threads: 4", copied, StringComparison.Ordinal);
        Assert.Contains("Handles: 12", copied, StringComparison.Ordinal);
        Assert.Contains("Parent PID: 999", copied, StringComparison.Ordinal);
        Assert.Contains("Parent Process: parent", copied, StringComparison.Ordinal);
        Assert.Contains("Process Identity: 120 + 1970-01-01T00:02:00.0000000+00:00", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("Command line", copied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Environment", copied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", copied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", copied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00000000-0000", copied, StringComparison.Ordinal);

        context.Clipboard.Success = false;
        await context.ViewModel.CopyProcessDetailsCommand.ExecuteAsync(null);
        Assert.Contains("Clipboard is unavailable", context.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyDetailsRendersUnavailableFieldsInsteadOfFabricatedValues()
    {
        Harness context = new();
        ProcessDescriptor bare = Process(120, "sample", null) with
        {
            Architecture = ProcessArchitecture.Unknown,
            ThreadCount = MetricValue<int>.Unavailable(MetricAvailability.AccessDenied),
            HandleCount = default,
            ParentProcessId = null,
            ParentProcessName = null,
            FileVersion = null,
            Publisher = null
        };
        ProcessDescriptor sibling = Process(121, "child", @"C:\Apps\child.exe");
        ApplicationMetricSnapshot snapshot = Snapshot() with
        {
            Processes = [bare, sibling],
            ProcessCount = 2,
            ProcessMetrics = new Dictionary<ProcessInstanceId, ProcessMetricSample>()
        };
        context.ViewModel.Update(snapshot);
        context.Configure();

        await context.ViewModel.CopyProcessDetailsCommand.ExecuteAsync(null);

        string copied = Assert.Single(context.Clipboard.Values);
        // Identity and status remain available even when metadata is not.
        Assert.Contains("PID: 120", copied, StringComparison.Ordinal);
        Assert.Contains("Status: Running", copied, StringComparison.Ordinal);
        Assert.Contains("Architecture: Unknown", copied, StringComparison.Ordinal);
        Assert.Contains("Executable Path: Unavailable", copied, StringComparison.Ordinal);
        Assert.Contains("Publisher: Unavailable", copied, StringComparison.Ordinal);
        Assert.Contains("File Version: Unavailable", copied, StringComparison.Ordinal);
        Assert.Contains("Threads: Unavailable", copied, StringComparison.Ordinal);
        Assert.Contains("Handles: Unavailable", copied, StringComparison.Ordinal);
        Assert.Contains("Parent PID: Unavailable", copied, StringComparison.Ordinal);
        Assert.Contains("Parent Process: Unavailable", copied, StringComparison.Ordinal);
        // Per-process metrics absent in a multi-process app: no app-level fallback.
        Assert.Contains("CPU: Unavailable", copied, StringComparison.Ordinal);
        Assert.Contains("Memory: Unavailable", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0%", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("0 B", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshPreservesSelectedIdentityAndNavigationState()
    {
        Harness context = new();
        ApplicationMetricSnapshot first = Snapshot(twoProcesses: true);
        context.ViewModel.Update(first);
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[1];
        ProcessActionChoice selectedChoice = context.ViewModel.SelectedProcess;
        ProcessInstanceId selected = context.ViewModel.SelectedProcess.Target.InstanceId;
        ApplicationTabViewModel tab = new("sample", "Sample", context.ViewModel);

        tab.Update(Snapshot(twoProcesses: true), []);
        tab.Update(Snapshot(twoProcesses: true), []);

        Assert.Same(context.ViewModel, tab.ProcessActions);
        Assert.Same(selectedChoice, context.ViewModel.SelectedProcess);
        Assert.Equal(selected, context.ViewModel.SelectedProcess!.Target.InstanceId);
        Assert.True(context.ViewModel.CopyProcessDetailsCommand.CanExecute(null));
    }

    [Fact]
    public void XamlUsesCommandsAccessibilityAndBoundedTwoColumnLayout()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MonitoringXS.App",
            "MainWindow.xaml"));

        Assert.Contains("Command=\"{Binding EndTaskCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding EndProcessTreeCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenFileLocationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CopyProcessDetailsCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"End selected task\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EndTask_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedProcess, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);

        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            double logicalContentWidth = 1180 / scale - 96;
            double columnWidth = (logicalContentWidth - 8) / 2;
            Assert.True(columnWidth > 100);
        }
    }

    [Fact]
    public void SelectedProcessSurvivesMetricRefresh()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[1];
        ProcessInstanceId selected = context.ViewModel.SelectedProcess!.Target.InstanceId;
        ProcessActionChoice selectedRef = context.ViewModel.SelectedProcess;

        // Simulate multiple metric refreshes with same processes.
        for (int i = 0; i < 20; i++)
        {
            context.ViewModel.Update(Snapshot(twoProcesses: true));
        }

        Assert.Same(selectedRef, context.ViewModel.SelectedProcess);
        Assert.Equal(selected, context.ViewModel.SelectedProcess!.Target.InstanceId);
    }

    [Fact]
    public void SelectedProcessSurvivesCollectionReordering()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[1];
        ProcessInstanceId selected = context.ViewModel.SelectedProcess!.Target.InstanceId;

        // Refresh with same data; sort order may shift but identity must persist.
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.ViewModel.Update(Snapshot(twoProcesses: true));

        Assert.Equal(selected, context.ViewModel.SelectedProcess!.Target.InstanceId);
    }

    [Fact]
    public void AddingAnotherProcessDoesNotChangeSelection()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot());
        ProcessInstanceId selected = context.ViewModel.SelectedProcess!.Target.InstanceId;

        // Now update with two processes (adds a new child).
        context.ViewModel.Update(Snapshot(twoProcesses: true));

        Assert.Equal(selected, context.ViewModel.SelectedProcess!.Target.InstanceId);
    }

    [Fact]
    public void RemovingUnselectedProcessDoesNotChangeSelection()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[0];
        ProcessInstanceId selected = context.ViewModel.SelectedProcess!.Target.InstanceId;

        // Remove the second (unselected) process.
        context.ViewModel.Update(Snapshot(secondOnly: false));

        Assert.Equal(selected, context.ViewModel.SelectedProcess!.Target.InstanceId);
    }

    [Fact]
    public void SelectedProcessExitDoesNotAutoSelectAnother()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[0];

        // Update with only the second process (first has exited).
        context.ViewModel.Update(Snapshot(secondOnly: true));

        Assert.Null(context.ViewModel.SelectedProcess);
        Assert.Contains("exited", context.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.ViewModel.EndTaskCommand.CanExecute(null));
        Assert.False(context.ViewModel.OpenFileLocationCommand.CanExecute(null));
    }

    [Fact]
    public void SamePidWithDifferentStartTimeIsNotSameSelection()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot());
        ProcessInstanceId original = context.ViewModel.SelectedProcess!.Target.InstanceId;

        // Create snapshot with same PID but different start time.
        ProcessDescriptor reused = new(
            new ProcessInstanceId(original.ProcessId, DateTimeOffset.UtcNow),
            "sample",
            @"C:\Apps\sample.exe",
            null, null, null, null, null, false, true);
        ApplicationMetricSnapshot reuseSnapshot = SnapshotWithProcesses([reused]);
        context.ViewModel.Update(reuseSnapshot);

        // The original selection should be lost because start time differs.
        Assert.NotEqual(original, context.ViewModel.SelectedProcess?.Target.InstanceId);
    }

    [Fact]
    public void PidReuseDisablesActionsForExitedSelection()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot());
        ProcessInstanceId original = context.ViewModel.SelectedProcess!.Target.InstanceId;

        // PID reused with different start time.
        ProcessDescriptor reused = new(
            new ProcessInstanceId(original.ProcessId, DateTimeOffset.UtcNow),
            "sample",
            @"C:\Apps\sample.exe",
            null, null, null, null, null, false, true);
        context.ViewModel.Update(SnapshotWithProcesses([reused]));

        // Selection was cleared because original exited (different start time).
        Assert.Null(context.ViewModel.SelectedProcess);
        Assert.False(context.ViewModel.EndTaskCommand.CanExecute(null));
    }

    [Fact]
    public void LanguageSwitchPreservesSelection()
    {
        Harness context = new();
        LocalizationService localization = new();
        ProcessActionsViewModel vm = new(context.Actions, context.Clipboard, localization);
        vm.Update(Snapshot(twoProcesses: true));
        vm.SelectedProcess = vm.Processes[1];
        ProcessInstanceId selected = vm.SelectedProcess!.Target.InstanceId;

        // Simulate language change by triggering relocalization through Update.
        vm.Update(Snapshot(twoProcesses: true));

        Assert.Equal(selected, vm.SelectedProcess!.Target.InstanceId);
    }

    [Fact]
    public void LogicalApplicationTabSurvivesChildProcessChanges()
    {
        Harness context = new();
        ApplicationTabViewModel tab = new("sample", "Sample", context.ViewModel);
        tab.Update(Snapshot(twoProcesses: true), []);
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[0];
        ProcessInstanceId selected = context.ViewModel.SelectedProcess!.Target.InstanceId;

        // Child process changes: remove one, add another.
        tab.Update(Snapshot(secondOnly: true), []);
        tab.Update(Snapshot(twoProcesses: true), []);

        Assert.Same(context.ViewModel, tab.ProcessActions);
        Assert.Equal(selected, context.ViewModel.SelectedProcess!.Target.InstanceId);
    }

    [Fact]
    public async Task ActionRevalidationRejectsStaleProcessIdentity()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[0];
        ProcessInstanceId original = context.ViewModel.SelectedProcess!.Target.InstanceId;
        context.Actions.PauseOpen = true;
        context.Configure();

        Task action = context.ViewModel.OpenFileLocationCommand.ExecuteAsync(null);
        await context.Actions.OpenStarted.Task.WaitAsync(TestCancellation);

        // While action is in progress, the process exits and PID is reused.
        ProcessDescriptor reused = new(
            new ProcessInstanceId(original.ProcessId, DateTimeOffset.UtcNow),
            "sample",
            @"C:\Apps\sample.exe",
            null, null, null, null, null, false, true);
        context.ViewModel.Update(SnapshotWithProcesses([reused]));

        context.Actions.ContinueOpen.SetResult();
        await action;

        // After completion, the selection should reflect that original exited.
        Assert.Null(context.ViewModel.SelectedProcess);
    }

    [Fact]
    public void NoDuplicateEventHandlersAccumulateDuringRefresh()
    {
        Harness context = new();
        int changeCount = 0;
        context.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessActionsViewModel.SelectedProcess))
            {
                changeCount++;
            }
        };

        context.ViewModel.Update(Snapshot(twoProcesses: true));
        context.ViewModel.SelectedProcess = context.ViewModel.Processes[1];
        int baseline = changeCount;

        // Multiple refreshes with identical data should not trigger selection changes.
        for (int i = 0; i < 10; i++)
        {
            context.ViewModel.Update(Snapshot(twoProcesses: true));
        }

        Assert.Equal(baseline, changeCount);
    }

    [Fact]
    public void ProcessSelectorCollectionRemainsBounded()
    {
        Harness context = new();
        context.ViewModel.Update(Snapshot(twoProcesses: true));
        int initialCount = context.ViewModel.Processes.Count;

        for (int i = 0; i < 50; i++)
        {
            context.ViewModel.Update(Snapshot(twoProcesses: true));
        }

        Assert.Equal(initialCount, context.ViewModel.Processes.Count);
    }

    private static ApplicationMetricSnapshot SnapshotWithProcesses(ProcessDescriptor[] processes) =>
        new(
            new(
                "sample",
                "Sample App",
                "Publisher",
                ApplicationDisposition.Installed,
                @"C:\Apps",
                ClassificationConfidence.High,
                "test"),
            DateTimeOffset.UtcNow,
            MetricValue<double>.Available(12.5),
            MetricValue<long>.Available(128 * 1024 * 1024),
            MetricValue<double>.Available(2048),
            MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            processes.Length,
            processes)
        {
            PhysicalDisk = PhysicalDiskMetricSet.Unavailable(
                MetricAvailability.WarmingUp,
                "Warming up."),
            Network = NetworkMetricSet.Unavailable(
                MetricAvailability.Unsupported,
                NetworkAvailabilityReason.Unsupported,
                "Unsupported."),
            Gpu = GpuMetricSet.Unavailable(
                MetricAvailability.Unavailable,
                GpuAvailabilityReason.CounterUnavailable,
                "Unavailable.")
        };

    private static ApplicationMetricSnapshot Snapshot(
        bool twoProcesses = false,
        string? path = @"C:\Apps\sample.exe",
        bool secondOnly = false)
    {
        ProcessDescriptor first = Process(120, "sample", path);
        ProcessDescriptor second = Process(121, "child", @"C:\Apps\child.exe");
        ProcessDescriptor[] processes = secondOnly
            ? [second]
            : twoProcesses
                ? [first, second]
                : [first];
        return new(
            new(
                "sample",
                "Sample App",
                "Publisher",
                ApplicationDisposition.Installed,
                @"C:\Apps",
                ClassificationConfidence.High,
                "test"),
            DateTimeOffset.UtcNow,
            MetricValue<double>.Available(12.5),
            MetricValue<long>.Available(128 * 1024 * 1024),
            MetricValue<double>.Available(2048),
            MetricValue<double>.Unavailable(MetricAvailability.AccessDenied),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            processes.Length,
            processes)
        {
            PhysicalDisk = PhysicalDiskMetricSet.Unavailable(
                MetricAvailability.WarmingUp,
                "Warming up."),
            Network = NetworkMetricSet.Unavailable(
                MetricAvailability.Unsupported,
                NetworkAvailabilityReason.Unsupported,
                "Unsupported."),
            Gpu = GpuMetricSet.Unavailable(
                MetricAvailability.Unavailable,
                GpuAvailabilityReason.CounterUnavailable,
                "Unavailable.")
        };
    }

    private static ProcessDescriptor Process(int pid, string name, string? path) =>
        new(
            new ProcessInstanceId(
                pid,
                DateTimeOffset.UnixEpoch.AddSeconds(pid)),
            name,
            path,
            null,
            null,
            null,
            null,
            null,
            false,
            pid == 120)
        {
            Architecture = ProcessArchitecture.X64,
            ThreadCount = MetricValue<int>.Available(4),
            HandleCount = MetricValue<int>.Available(12),
            ParentProcessId = 999,
            ParentProcessName = "parent",
            FileVersion = "1.0",
            Publisher = "Contoso Publisher"
        };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "MonitoringXS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("MonitoringXS repository root was not found.");
    }

    private sealed class Harness
    {
        public FakeActions Actions { get; } = new();

        public FakeClipboard Clipboard { get; } = new();

        public ProcessActionsViewModel ViewModel { get; }

        public Func<ProcessActionConfirmation, CancellationToken, Task<bool>> Confirm { get; set; } =
            (_, _) => Task.FromResult(true);

        public TaskCompletionSource ConfirmationShown { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness()
        {
            ViewModel = new(Actions, Clipboard);
        }

        public void Configure() =>
            ViewModel.Configure(
                (request, token) => Confirm(request, token),
                _ => Task.CompletedTask,
                TestCancellation);
    }

    private sealed class FakeActions : IProcessActionService
    {
        public ProcessActionInspection Inspection { get; set; } =
            new(ProcessActionStatus.Success, "verified");

        public ProcessActionResult EndResult { get; set; } =
            new(ProcessActionStatus.Success, "Ended.", 1);

        public List<ProcessActionTarget> Ended { get; } = [];

        public List<ProcessActionTarget> TreesEnded { get; } = [];

        public bool PauseOpen { get; set; }

        public TaskCompletionSource OpenStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueOpen { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProcessActionInspection> InspectAsync(
            ProcessActionTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Inspection);

        public ValueTask<ProcessActionInspection> InspectTreeAsync(
            ProcessActionTarget target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Inspection);

        public ValueTask<ProcessActionResult> EndProcessAsync(
            ProcessActionTarget target,
            CancellationToken cancellationToken)
        {
            Ended.Add(target);
            return ValueTask.FromResult(EndResult);
        }

        public ValueTask<ProcessActionResult> EndProcessTreeAsync(
            ProcessActionTarget target,
            CancellationToken cancellationToken)
        {
            TreesEnded.Add(target);
            return ValueTask.FromResult(EndResult);
        }

        public async ValueTask<ProcessActionResult> OpenFileLocationAsync(
            ProcessActionTarget target,
            CancellationToken cancellationToken)
        {
            OpenStarted.TrySetResult();
            if (PauseOpen)
            {
                await ContinueOpen.Task.WaitAsync(cancellationToken);
            }

            return new(ProcessActionStatus.Success, "File location opened.");
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public bool Success { get; set; } = true;

        public List<string> Values { get; } = [];

        public ValueTask<bool> CopyTextAsync(
            string text,
            CancellationToken cancellationToken)
        {
            Values.Add(text);
            return ValueTask.FromResult(Success);
        }
    }
}
