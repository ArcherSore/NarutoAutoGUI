using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Views;

// Decides what the runtime control card offers; MainWindow applies the result and performs every side effect.
internal static class RuntimeControls
{
    internal static RuntimeControlState Derive(RuntimeControlInputs inputs)
    {
        var (session, observed, project, pending, preparationFailed, exiting, ending, endUnconfirmed) = inputs;
        var commandsAvailable = pending == PendingOperation.None && !exiting && !ending
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
        var starting = active is { State: RunState.Starting } startingRun && !IsStoppable(startingRun)
            || pending == PendingOperation.StartingRun;
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
        // Retry prepares again when the Runtime Environment failed or is not ready, and starts the Run again once it
        // is ready. A snapshot that is still resynchronising is only waited for.
        var resynchronising = observed is { Observation: WorkerObservation.Connected, SnapshotFresh: false };
        var retryTarget = runtimeFaulted || !environmentReady && !resynchronising
            ? RetryTarget.Prepare : RetryTarget.Start;
        var retryEnabled = retryTarget == RetryTarget.Prepare ? commandsAvailable && project.Loaded : startAllowed;
        // Execution termination is unconfirmed while a Worker may still run something the GUI cannot reach. Only
        // ending the Runtime Environment helps then; Retry would loop on the same Worker or start a second one.
        // A lost Worker is described as lost even when its last snapshot was Faulted.
        RecoveryReason? recovery = endUnconfirmed ? RecoveryReason.EndUnconfirmed
            : observed.Observation is WorkerObservation.IpcDisconnected or WorkerObservation.WorkerRecoveryConflict
                ? RecoveryReason.WorkerLost
            : worker?.WorkerState == WorkerState.Faulted
                ? active is null ? RecoveryReason.RuntimeFaulted : RecoveryReason.StopUnconfirmed
            : null;
        var progress = new RuntimeAction.InProgress(ending ? ProgressKind.EndingEnvironment
            : stopping ? ProgressKind.StoppingRun
            : starting ? ProgressKind.StartingRun : ProgressKind.PreparingEnvironment);
        // Progress the GUI drives outranks recovery. Progress a Worker reports does not, because a Faulted or lost
        // Worker may never finish it.
        var guiProgress = ending || preparing
            || pending is PendingOperation.StartingRun or PendingOperation.StoppingRun;
        RuntimeAction action = guiProgress ? progress
            : recovery is { } reason ? new RuntimeAction.Recover(commandsAvailable, reason)
            : starting || stopping ? progress
            : active?.State == RunState.Running || active is not null && IsStoppable(active)
                ? new RuntimeAction.Stop(stopAllowed)
            : runtimeFaulted || runFaulted ? new RuntimeAction.Retry(retryTarget, retryEnabled,
                retryTarget == RetryTarget.Start ? startBlocker : null)
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
            sessionConnected && commandsAvailable,
            DesktopStatusOf(session, action is RuntimeAction.InProgress { Kind: ProgressKind.PreparingEnvironment }));
        return new RuntimeControlState(action, editable, configurationLock, desktop, commandsAvailable);
    }

    // What the desktop toggle reports. It follows the Child Session, so a toggle that is briefly unavailable during
    // another operation still shows a running desktop as running. Preparation opens the viewer once RDP connects,
    // yet the desktop counts as connecting until the Runtime Environment is ready; unlocked input always shows.
    private static DesktopStatus DesktopStatusOf(ChildSessionSnapshot session, bool preparing)
    {
        if (session.InputUnlocked) {
            return DesktopStatus.Interactive;
        }
        return session.State switch {
            ChildSessionState.Existing or ChildSessionState.Connecting => DesktopStatus.Connecting,
            ChildSessionState.ConnectedHidden or ChildSessionState.ConnectedVisible when preparing
                => DesktopStatus.Connecting,
            ChildSessionState.ConnectedHidden => DesktopStatus.Background,
            ChildSessionState.ConnectedVisible => DesktopStatus.Viewing,
            ChildSessionState.Disconnecting => DesktopStatus.Ending,
            ChildSessionState.Faulted => DesktopStatus.Disconnected,
            _ => DesktopStatus.NotRunning
        };
    }

    // The Stop operation rechecks this when it runs, because the Run may move on while the operation waits.
    internal static bool IsStoppable(RunSnapshot run) => run.Items.Any(item =>
        item.State is PlanItemState.Starting or PlanItemState.Running
        && (run.State == RunState.Running || run.State == RunState.Starting && item.Kind == PlanItemKind.Pretask));
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
    PendingOperation PendingOperation, bool EnvironmentPreparationFailed, bool Exiting, bool Ending,
    bool EndUnconfirmed);

internal enum RetryTarget
{
    Prepare, Start
}

internal enum ProgressKind
{
    PreparingEnvironment, StartingRun, StoppingRun, EndingEnvironment
}

// Why Start cannot run, listed in the order checked so the reason a user can act on comes first.
internal enum StartBlocker
{
    CommandsUnavailable, ProjectNotLoaded, NoTasks, ConfigurationInvalid, RuntimeNotReady
}

// Why only ending the Runtime Environment can recover, in the words the recovery action explains it with.
internal enum RecoveryReason
{
    StopUnconfirmed, RuntimeFaulted, WorkerLost, EndUnconfirmed
}

internal enum LockReason
{
    RunActive, RuntimeBusy
}

internal enum DesktopAction
{
    Show, Hide
}

internal enum DesktopStatus
{
    NotRunning, Connecting, Background, Viewing, Interactive, Ending, Disconnected
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

    internal sealed record Recover(bool Enabled, RecoveryReason Reason) : RuntimeAction;
}

// Action is what a click does and Status what the toggle reports; they differ while preparation shows the viewer.
internal sealed record DesktopToggle(DesktopAction Action, bool Enabled, DesktopStatus Status);

// ConfigurationLock is the lock badge reason; it is null while editing is allowed or no project is loaded.
internal sealed record RuntimeControlState(
    RuntimeAction Action, bool ConfigurationEditable, LockReason? ConfigurationLock, DesktopToggle Desktop,
    bool CommandsAvailable);
