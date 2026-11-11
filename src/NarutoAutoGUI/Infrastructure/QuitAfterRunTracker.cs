using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

// Tells when the Run that --quit-after-run waits for has ended, however it ended. A Run that started before the
// tracker was armed, a stale snapshot, and a Worker that never confirmed the stop (Faulted keeps the Active Run) are
// not endings.
internal sealed class QuitAfterRunTracker(DateTime armedAtUtc)
{
    private bool _triggered;
    private Guid? _runId;

    // Narrows the wait to one Run, for a start request whose answer was lost.
    internal void Await(Guid runId) => _runId = runId;

    internal bool Observe(WorkerCoordinatorSnapshot observation)
    {
        if (_triggered || !observation.SnapshotFresh
            || observation.WorkerSnapshot?.LastRun is not { } run || run.StartedAtUtc < armedAtUtc
            || _runId is { } runId && run.RunId != runId
            || run.State is not (RunState.Succeeded or RunState.Failed or RunState.Cancelled)) {
            return false;
        }
        return _triggered = true;
    }
}
