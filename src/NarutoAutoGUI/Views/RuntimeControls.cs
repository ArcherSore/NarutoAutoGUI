using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Views;

// Decides what the runtime control card offers; MainWindow applies the result and performs every side effect.
internal static class RuntimeControls
{
    internal static RuntimeControlState Derive(RuntimeControlInputs inputs)
    {
        var (session, observed, project, pending, preparationFailed, exiting) = inputs;
        var commandsAvailable = pending == PendingOperation.None && !exiting
            && session.State is not (ChildSessionState.Connecting or ChildSessionState.Disconnecting);
        var sessionConnected = session.State is ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden;
        var fresh = observed is { Observation: WorkerObservation.Connected, SnapshotFresh: true };
        // A confirmed ended runtime keeps its last snapshot for diagnostics, but that snapshot cannot own controls.
        var worker = observed.Observation is WorkerObservation.ChildSessionEnded or WorkerObservation.WorkerExited
            ? null : observed.WorkerSnapshot;
        var active = worker?.ActiveRun;
        var environmentReady = sessionConnected && project.Loaded && fresh && worker?.WorkerState == WorkerState.Ready
            && worker.RuntimeProfileDigest == project.RuntimeProfileDigest;
        var runStartable = environmentReady && project is { SelectedTaskCount: > 0, ConfigurationValid: true }
            && worker is { ActiveRun: null, RunState: RunState.Idle };
        // Stopping needs only the Run itself, never the project or the Active Configuration.
        var stopAllowed = commandsAvailable && fresh && active is { } run && IsStoppable(run);

        var preparing = pending == PendingOperation.PreparingEnvironment
            || session.State is ChildSessionState.Connecting or ChildSessionState.Existing
            || observed.Observation == WorkerObservation.WorkerStarting || worker?.WorkerState == WorkerState.Starting;
        var starting = active?.State == RunState.Starting || pending == PendingOperation.StartingRun;
        var stopping = active?.State == RunState.Stopping || pending == PendingOperation.StoppingRun;
        var runtimeFaulted = preparationFailed || session.State == ChildSessionState.Faulted
            || observed.Observation is WorkerObservation.IpcDisconnected or WorkerObservation.WorkerExited
                or WorkerObservation.WorkerRecoveryConflict
            || worker?.WorkerState == WorkerState.Faulted;
        var runFaulted = active is null && worker?.LastRun?.State == RunState.Failed;
        var startAllowed = commandsAvailable && runStartable;
        StartBlocker? startBlocker = startAllowed ? null
            : !commandsAvailable ? StartBlocker.CommandsUnavailable
            : !project.Loaded ? StartBlocker.ProjectNotLoaded
            : project.SelectedTaskCount == 0 ? StartBlocker.NoTasks
            : !project.ConfigurationValid ? StartBlocker.ConfigurationInvalid
            : StartBlocker.RuntimeNotReady;
        // Retry prepares again only when the Runtime Environment failed; after a failed Run it is Start again.
        var retryTarget = runtimeFaulted ? RetryTarget.Prepare : RetryTarget.Start;
        var retryEnabled = retryTarget == RetryTarget.Prepare ? commandsAvailable && project.Loaded : startAllowed;
        RuntimeAction action = preparing || starting || stopping
            ? new RuntimeAction.InProgress(stopping ? ProgressKind.StoppingRun
                : starting ? ProgressKind.StartingRun : ProgressKind.PreparingEnvironment)
            : active?.State == RunState.Running ? new RuntimeAction.Stop(stopAllowed)
            : runtimeFaulted || runFaulted ? new RuntimeAction.Retry(retryTarget, retryEnabled,
                retryTarget == RetryTarget.Start && !retryEnabled ? startBlocker : null)
            : environmentReady ? new RuntimeAction.Start(startAllowed, startBlocker)
            : new RuntimeAction.Prepare(commandsAvailable && project.Loaded);

        // With no Worker, or one proven gone, no Run can be using the configuration; a Worker that may still be
        // alive keeps it locked until a fresh snapshot shows it idle.
        var editable = project.Loaded && commandsAvailable
            && (observed.Observation is WorkerObservation.WorkerNotStarted or WorkerObservation.ChildSessionEnded
                    or WorkerObservation.WorkerExited
                || fresh && worker is { ActiveRun: null, RunState: RunState.Idle });
        LockReason? configurationLock = !project.Loaded || editable ? null
            : active is not null || pending == PendingOperation.StartingRun ? LockReason.RunActive
            : LockReason.RuntimeBusy;
        var desktop = new DesktopToggle(
            session.State == ChildSessionState.ConnectedVisible ? DesktopAction.Hide : DesktopAction.Show,
            sessionConnected && commandsAvailable);
        Guid? previewWorker = !exiting && !preparationFailed && pending != PendingOperation.PreparingEnvironment
            && sessionConnected && fresh && observed.WorkerSnapshot is { WorkerState: WorkerState.Ready } snapshot
            && snapshot.ChildSessionId == session.ChildSessionId ? snapshot.WorkerInstanceId : null;
        return new RuntimeControlState(action, editable, configurationLock, desktop, commandsAvailable, previewWorker);
    }

    // The Stop operation rechecks this when it runs, because the Run may move on while the operation waits.
    internal static bool IsStoppable(RunSnapshot run) => run.State == RunState.Running
        && run.Items.Any(item => item.State is PlanItemState.Starting or PlanItemState.Running);
}

// The GUI operation in progress; MainWindow runs at most one at a time.
internal enum PendingOperation
{
    None, RestoringSession, ShowingDesktop, PreparingEnvironment, StartingRun, StoppingRun
}

internal sealed record ProjectReadiness(
    bool Loaded, int SelectedTaskCount, bool ConfigurationValid, string? RuntimeProfileDigest)
{
    internal static ProjectReadiness NotLoaded { get; } = new(false, 0, false, null);
}

internal sealed record RuntimeControlInputs(
    ChildSessionSnapshot Session, WorkerCoordinatorSnapshot Worker, ProjectReadiness Project,
    PendingOperation PendingOperation, bool EnvironmentPreparationFailed, bool Exiting);

internal enum RetryTarget
{
    Prepare, Start
}

internal enum ProgressKind
{
    PreparingEnvironment, StartingRun, StoppingRun
}

// Why Start cannot run, listed in the order checked so the reason a user can act on comes first.
internal enum StartBlocker
{
    CommandsUnavailable, ProjectNotLoaded, NoTasks, ConfigurationInvalid, RuntimeNotReady
}

internal enum LockReason
{
    RunActive, RuntimeBusy
}

internal enum DesktopAction
{
    Show, Hide
}

// The single primary action the runtime control card offers; the private constructor closes the set.
internal abstract record RuntimeAction
{
    private RuntimeAction()
    {
    }

    internal sealed record Prepare(bool Enabled) : RuntimeAction;

    internal sealed record Retry(RetryTarget Target, bool Enabled, StartBlocker? Blocker = null) : RuntimeAction;

    internal sealed record Start(bool Enabled, StartBlocker? Blocker = null) : RuntimeAction;

    internal sealed record Stop(bool Enabled) : RuntimeAction;

    internal sealed record InProgress(ProgressKind Kind) : RuntimeAction;
}

internal sealed record DesktopToggle(DesktopAction Action, bool Enabled);

// ConfigurationLock is the lock badge reason; it is null while editing is allowed or no project is loaded.
internal sealed record RuntimeControlState(
    RuntimeAction Action, bool ConfigurationEditable, LockReason? ConfigurationLock, DesktopToggle Desktop,
    bool CommandsAvailable, Guid? PreviewWorker);
