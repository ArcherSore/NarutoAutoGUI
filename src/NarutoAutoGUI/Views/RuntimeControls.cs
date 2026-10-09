using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Views;

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

    internal sealed record Retry(RetryTarget Target, bool Enabled) : RuntimeAction;

    internal sealed record Start(bool Enabled) : RuntimeAction;

    internal sealed record Stop(bool Enabled) : RuntimeAction;

    internal sealed record InProgress(ProgressKind Kind) : RuntimeAction;
}

internal sealed record DesktopToggle(DesktopAction Action, bool Enabled);

// ConfigurationLock is the lock badge reason; it is null while editing is allowed or no project is loaded.
internal sealed record RuntimeControlState(
    RuntimeAction Action, bool ConfigurationEditable, LockReason? ConfigurationLock, DesktopToggle Desktop,
    bool CommandsAvailable, Guid? PreviewWorker);
