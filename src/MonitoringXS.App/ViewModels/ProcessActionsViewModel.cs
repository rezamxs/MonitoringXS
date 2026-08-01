using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.ViewModels;

public sealed class ProcessActionChoice
{
    public ProcessActionChoice(
        string label,
        ProcessActionTarget target,
        ProcessDescriptor process)
    {
        Label = label;
        Target = target;
        Process = process;
    }

    public string Label { get; }

    public ProcessActionTarget Target { get; }

    public ProcessDescriptor Process { get; }
}

public sealed record ProcessActionConfirmation(
    string Title,
    string Message,
    string ConfirmButtonText);

public sealed partial class ProcessActionsViewModel : ObservableObject
{
    private readonly IProcessActionService _actions;
    private readonly IClipboardService _clipboard;
    private Func<ProcessActionConfirmation, CancellationToken, Task<bool>>? _confirm;
    private Func<CancellationToken, Task>? _refresh;
    private CancellationToken _shutdownToken;
    private ApplicationMetricSnapshot? _snapshot;
    private string? _lastActionFeedback;
    private long _selectionVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndProcessTreeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyProcessDetailsCommand))]
    public partial ProcessActionChoice? SelectedProcess { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndProcessTreeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyProcessDetailsCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ProcessActionChoice> Processes { get; set; } =
        Array.Empty<ProcessActionChoice>();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Select a process.";

    [ObservableProperty]
    public partial string IdentityText { get; set; } = "No process selected";

    [ObservableProperty]
    public partial string ExecutablePathText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial string StartTimeText { get; set; } = "Unavailable";

    public ProcessActionsViewModel(
        IProcessActionService actions,
        IClipboardService clipboard)
    {
        _actions = actions;
        _clipboard = clipboard;
    }

    internal void Configure(
        Func<ProcessActionConfirmation, CancellationToken, Task<bool>> confirm,
        Func<CancellationToken, Task> refresh,
        CancellationToken shutdownToken)
    {
        _confirm = confirm;
        _refresh = refresh;
        _shutdownToken = shutdownToken;
        NotifyCommands();
    }

    public void Update(ApplicationMetricSnapshot snapshot)
    {
        ProcessActionChoice? previous = SelectedProcess;
        ProcessInstanceId? selected = previous?.Target.InstanceId;
        Dictionary<ProcessInstanceId, ProcessActionChoice> existing =
            Processes.ToDictionary(choice => choice.Target.InstanceId);
        _snapshot = snapshot;
        ProcessActionChoice[] choices = snapshot.Processes
            .OrderByDescending(process => process.HasVisibleWindow)
            .ThenBy(process => process.NormalizedProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.InstanceId.ProcessId)
            .Select(process =>
                existing.TryGetValue(process.InstanceId, out ProcessActionChoice? choice)
                    ? choice
                    : new ProcessActionChoice(
                        $"{process.NormalizedProcessName} (PID {process.InstanceId.ProcessId})",
                        new ProcessActionTarget(
                            process.InstanceId,
                            snapshot.Application.DisplayName,
                            process.NormalizedProcessName,
                            process.ExecutablePath),
                        process))
            .ToArray();
        ProcessActionChoice? next =
            choices.FirstOrDefault(choice => choice.Target.InstanceId == selected);
        if (next is null && IsBusy && previous is not null)
        {
            choices = [previous, .. choices];
            next = previous;
        }

        Processes = choices;
        SelectedProcess = next ?? choices.FirstOrDefault();
    }

    partial void OnSelectedProcessChanged(
        ProcessActionChoice? oldValue,
        ProcessActionChoice? newValue)
    {
        bool identityChanged =
            oldValue?.Target.InstanceId != newValue?.Target.InstanceId;
        if (identityChanged)
        {
            Interlocked.Increment(ref _selectionVersion);
        }

        if (newValue is null)
        {
            IdentityText = "No process selected";
            ExecutablePathText = "Unavailable";
            StartTimeText = "Unavailable";
            if (identityChanged)
            {
                StatusText = "Select a process.";
            }

            return;
        }

        IdentityText = $"{newValue.Target.ProcessName} · PID {newValue.Target.InstanceId.ProcessId}";
        ExecutablePathText = newValue.Process.ExecutablePath ?? "Unavailable";
        StartTimeText = newValue.Target.InstanceId.StartTimeUtc
            .ToLocalTime()
            .ToString("G", System.Globalization.CultureInfo.CurrentCulture);
        if (identityChanged)
        {
            StatusText = _lastActionFeedback is null
                ? "Ready."
                : $"Ready. Last action: {_lastActionFeedback}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunDestructiveAction))]
    private async Task EndTaskAsync()
    {
        ProcessActionChoice? selected = SelectedProcess;
        if (selected is null || _confirm is null)
        {
            return;
        }

        long version = _selectionVersion;
        IsBusy = true;
        try
        {
            bool confirmed = await _confirm(
                new(
                    "End task?",
                    $"End {selected.Target.DisplayName} ({selected.Target.ProcessName}, PID {selected.Target.InstanceId.ProcessId})? Unsaved work may be lost.",
                    "End task"),
                _shutdownToken);
            if (!confirmed)
            {
                SetCurrentStatus(version, "End Task cancelled.");
                return;
            }

            await RunActionCoreAsync(
                version,
                "Ending task…",
                token => _actions.EndProcessAsync(selected.Target, token),
                refreshAfterCompletion: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunDestructiveAction))]
    private async Task EndProcessTreeAsync()
    {
        ProcessActionChoice? selected = SelectedProcess;
        if (selected is null || _confirm is null)
        {
            return;
        }

        long version = _selectionVersion;
        IsBusy = true;
        StatusText = "Verifying process tree…";
        try
        {
            ProcessActionInspection inspection =
                await _actions.InspectTreeAsync(selected.Target, _shutdownToken);
            if (!inspection.IsValid)
            {
                SetCurrentStatus(version, inspection.Message);
                return;
            }

            bool confirmed = await _confirm(
                new(
                    "End process tree?",
                    $"End {selected.Target.DisplayName} ({selected.Target.ProcessName}, PID {selected.Target.InstanceId.ProcessId}) and {inspection.DescendantCount} currently identified descendant{(inspection.DescendantCount == 1 ? string.Empty : "s")}? Unsaved work in every process may be lost.",
                    "End process tree"),
                _shutdownToken);
            if (!confirmed)
            {
                SetCurrentStatus(version, "End Process Tree cancelled.");
                return;
            }

            await RunActionCoreAsync(
                version,
                "Ending process tree…",
                token => _actions.EndProcessTreeAsync(selected.Target, token),
                refreshAfterCompletion: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenFileLocation))]
    private async Task OpenFileLocationAsync()
    {
        ProcessActionChoice? selected = SelectedProcess;
        if (selected is null)
        {
            return;
        }

        await RunActionAsync(
            selected,
            _selectionVersion,
            "Opening file location…",
            token => _actions.OpenFileLocationAsync(selected.Target, token),
            refreshAfterCompletion: false);
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task CopyProcessDetailsAsync()
    {
        ProcessActionChoice? selected = SelectedProcess;
        ApplicationMetricSnapshot? snapshot = _snapshot;
        if (selected is null || snapshot is null)
        {
            return;
        }

        long version = _selectionVersion;
        IsBusy = true;
        StatusText = "Verifying process details…";
        try
        {
            ProcessActionInspection inspection =
                await _actions.InspectAsync(selected.Target, _shutdownToken);
            if (!inspection.IsValid)
            {
                SetCurrentStatus(version, inspection.Message);
                return;
            }

            string details = ProcessDetailsTextFormatter.Format(
                snapshot,
                selected.Process,
                DateTimeOffset.UtcNow);
            bool copied = await _clipboard.CopyTextAsync(details, _shutdownToken);
            SetCurrentStatus(
                version,
                copied
                    ? "Process details copied."
                    : "Clipboard is unavailable; process details were not copied.");
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunActionAsync(
        ProcessActionChoice selected,
        long version,
        string progress,
        Func<CancellationToken, ValueTask<ProcessActionResult>> action,
        bool refreshAfterCompletion)
    {
        IsBusy = true;
        try
        {
            await RunActionCoreAsync(
                version,
                progress,
                action,
                refreshAfterCompletion);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunActionCoreAsync(
        long version,
        string progress,
        Func<CancellationToken, ValueTask<ProcessActionResult>> action,
        bool refreshAfterCompletion)
    {
        StatusText = progress;
        ProcessActionResult result = await action(_shutdownToken);
        SetCurrentStatus(version, result.Message);
        if (refreshAfterCompletion
            && result.Status is (
                ProcessActionStatus.Success
                or ProcessActionStatus.AlreadyExited
                or ProcessActionStatus.PartialTreeTermination)
            && _refresh is not null)
        {
            await _refresh(_shutdownToken);
        }
    }

    private bool CanRunAction() =>
        !IsBusy && SelectedProcess is not null;

    private bool CanRunDestructiveAction() =>
        CanRunAction() && _confirm is not null;

    private bool CanOpenFileLocation() =>
        CanRunAction()
        && !string.IsNullOrWhiteSpace(SelectedProcess!.Target.ExpectedExecutablePath);

    private void SetCurrentStatus(long version, string status)
    {
        if (version == _selectionVersion)
        {
            _lastActionFeedback = status;
            StatusText = status;
        }
    }

    private void NotifyCommands()
    {
        EndTaskCommand.NotifyCanExecuteChanged();
        EndProcessTreeCommand.NotifyCanExecuteChanged();
        OpenFileLocationCommand.NotifyCanExecuteChanged();
        CopyProcessDetailsCommand.NotifyCanExecuteChanged();
    }
}
