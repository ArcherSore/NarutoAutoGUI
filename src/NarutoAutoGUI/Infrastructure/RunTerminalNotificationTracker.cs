using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal sealed record RunTerminalNotification(string Title, string Message, bool Failed);

internal sealed class RunTerminalNotificationTracker
{
    private readonly HashSet<Guid> _observedRuns = [];
    private readonly HashSet<Guid> _completedRuns = [];

    internal RunTerminalNotification? Observe(WorkerCoordinatorSnapshot observation)
    {
        if (!observation.SnapshotFresh || observation.WorkerSnapshot is not { } snapshot) {
            return null;
        }
        if (snapshot.ActiveRun is { } active && !_completedRuns.Contains(active.RunId)) {
            _observedRuns.Add(active.RunId);
        }
        if (snapshot.LastRun is not { State: RunState.Succeeded or RunState.Failed or RunState.Cancelled } run
            || !_observedRuns.Remove(run.RunId) || !_completedRuns.Add(run.RunId)) {
            return null;
        }
        if (run.State == RunState.Cancelled) {
            return null;
        }
        if (run.State == RunState.Succeeded) {
            return new RunTerminalNotification("任务已完成", "所有任务已执行完成。", false);
        }
        var failedItem = run.Items.FirstOrDefault(item => item.State == PlanItemState.Failed);
        var label = run.Plan.Items.FirstOrDefault(item => item.PlanItemId == failedItem?.PlanItemId)?.TaskLabel;
        var message = string.IsNullOrWhiteSpace(label)
            ? "任务运行失败。打开 NarutoAutoGUI 查看运行动态。"
            : $"失败任务：{label}。打开 NarutoAutoGUI 查看运行动态。";
        return new RunTerminalNotification("任务运行失败", message, true);
    }
}
