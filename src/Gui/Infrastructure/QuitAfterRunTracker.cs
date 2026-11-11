using Protocol;
using Gui.Worker;

namespace Gui.Infrastructure;

// Tells when the Run that --quit-after-run waits for has ended, however it ended. A Run that started before the
// tracker was armed, a stale snapshot, and a Worker that never confirmed the stop (Faulted keeps the Active Run) are
// not endings. The owner discards the tracker once it fires.
internal sealed class QuitAfterRunTracker(DateTime armedAtUtc)
{
    private Guid? _runId;

    // Narrows the wait to one Run, for a start request whose answer was lost.
    internal void Await(Guid runId) => _runId = runId;

    internal bool Observe(WorkerCoordinatorSnapshot observation) => observation.SnapshotFresh
        && observation.WorkerSnapshot?.LastRun is { } run && !(run.StartedAtUtc < armedAtUtc)
        && (_runId is not { } runId || run.RunId == runId)
        && run.State is RunState.Succeeded or RunState.Failed or RunState.Cancelled;
}
