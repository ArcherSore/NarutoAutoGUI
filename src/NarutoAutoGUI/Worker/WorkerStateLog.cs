using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Protocol;
using static NarutoAutoGUI.Infrastructure.AppLogger;

namespace NarutoAutoGUI.Worker;

// Writes changes in the GUI Observation and in the Worker State it carries. Reading the current snapshot under the gate
// keeps the lines in order when changes are raised from several threads.
internal sealed class WorkerStateLog(AppLogger logger)
{
    private readonly object _gate = new();
    private WorkerObservation? _observation;
    private string? _observationDetail;
    private (Guid Instance, WorkerState State, string? Code, string? Message)? _worker;

    internal void Observe(Func<WorkerCoordinatorSnapshot> current)
    {
        // The caller is the Worker IPC read loop: a logging fault must not end the connection.
        try {
            lock (_gate) {
                var snapshot = current();
                ReportObservation(snapshot);
                ReportWorkerState(snapshot.WorkerSnapshot);
            }
        } catch (Exception exception) {
            logger.Warn("写入 Worker 状态日志失败。", exception);
        }
    }

    private void ReportObservation(WorkerCoordinatorSnapshot snapshot)
    {
        // While connected the detail only narrates each state event, which the Run and Worker State lines cover.
        var detail = snapshot.Observation == WorkerObservation.Connected ? null : snapshot.Detail;
        if (_observation == snapshot.Observation && _observationDetail == detail) {
            return;
        }

        var worker = snapshot.WorkerSnapshot;
        // The observation can stay the same while its detail changes; a from equal to the to would only mislead.
        var from = _observation == snapshot.Observation ? null : _observation;
        var message = $"Worker 观察变化{Field("from", from)}{Field("to", snapshot.Observation)}"
            + $"{Field("detail", detail)}{Field("instance", worker?.WorkerInstanceId)}{Field("pid", worker?.WorkerPid)}"
            + $"{Field("session", worker?.ChildSessionId)}{Field("activeRun", worker?.ActiveRun?.RunId)}";
        _observation = snapshot.Observation;
        _observationDetail = detail;
        // Ending the Runtime Environment disconnects an idle Worker on purpose; losing one mid-Run is never expected.
        var unexpected = snapshot.Observation == WorkerObservation.WorkerRecoveryConflict
            || (snapshot.Observation is WorkerObservation.IpcDisconnected or WorkerObservation.WorkerExited
                && worker?.ActiveRun is not null);
        if (unexpected) {
            logger.Warn(message);
        } else {
            logger.Info(message);
        }
    }

    private void ReportWorkerState(WorkerSnapshot? worker)
    {
        if (worker is null) {
            return;
        }

        var reason = worker.WorkerReason;
        var state = (worker.WorkerInstanceId, worker.WorkerState, reason?.Code, reason?.Message);
        if (_worker == state) {
            return;
        }

        var previous = _worker?.Instance == worker.WorkerInstanceId ? _worker?.State : null;
        var message = $"Worker 状态变化{Field("from", previous)}{Field("to", worker.WorkerState)}"
            + $"{Field("code", reason?.Code)}{Field("message", reason?.Message.Trim())}"
            + $"{Field("instance", worker.WorkerInstanceId)}{Field("activeRun", worker.ActiveRun?.RunId)}";
        _worker = state;
        if (worker.WorkerState is WorkerState.Faulted or WorkerState.NotReady) {
            logger.Error(message);
        } else {
            logger.Info(message);
        }
    }
}
