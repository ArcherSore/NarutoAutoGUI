using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Views;

// What one Plan Item shows. A cancelled item that started was stopped; one that never started did not run.
internal enum PlanItemProgress
{
    Pending, Running, Succeeded, Failed, Stopped, NotRun
}

internal sealed record PlanItemProgressView(
    Guid PlanItemId, string TaskName, string TaskLabel, PlanItemProgress State, DateTime? StartedAtUtc,
    DateTime? EndedAtUtc);

// The progress of the active Run, or of the last Run while it still describes the plan on screen.
internal sealed record RunProgressView(
    bool Active, IReadOnlyList<PlanItemProgressView> Items, int? CurrentIndex, DateTime? StartedAtUtc,
    DateTime? EndedAtUtc)
{
    internal int Total => Items.Count;
    internal int Succeeded => Count(PlanItemProgress.Succeeded);
    internal int Failed => Count(PlanItemProgress.Failed);
    internal int Stopped => Count(PlanItemProgress.Stopped);
    internal int NotRun => Count(PlanItemProgress.NotRun);
    // Only finished items count as processed; a stopped or skipped item never does.
    internal int Processed => Succeeded + Failed;
    internal PlanItemProgressView? Current => CurrentIndex is { } index ? Items[index] : null;

    private int Count(PlanItemProgress state) => Items.Count(item => item.State == state);
}

// Kept apart from RuntimeControls.Derive: progress is presentation, while that decides what may be done.
internal static class RunProgress
{
    internal static RunProgressView? Derive(WorkerSnapshot? worker, IReadOnlyList<string> activeTaskNames)
    {
        if (worker?.ActiveRun is { } active) {
            return Create(active, true);
        }
        return worker?.LastRun is { } last
            && last.Plan.Items.Select(item => item.TaskName).SequenceEqual(activeTaskNames, StringComparer.Ordinal)
            ? Create(last, false) : null;
    }

    internal static PlanItemProgress Map(PlanItemSnapshot item) => item.State switch {
        PlanItemState.Pending => PlanItemProgress.Pending,
        PlanItemState.Starting or PlanItemState.Running => PlanItemProgress.Running,
        PlanItemState.Succeeded => PlanItemProgress.Succeeded,
        PlanItemState.Failed => PlanItemProgress.Failed,
        _ => item.StartedAtUtc is null ? PlanItemProgress.NotRun : PlanItemProgress.Stopped
    };

    // "已处理 2 / 5 · 2 成功" while running, "上次运行：3 成功 · 1 失败 · 已停止 · 1 未执行" afterwards.
    internal static string Caption(RunProgressView run)
    {
        var counts = $"{run.Succeeded} 成功{(run.Failed == 0 ? "" : $" · {run.Failed} 失败")}";
        if (run.Active) {
            return $"已处理 {run.Processed} / {run.Total} · {counts}";
        }
        var stopped = run.Stopped == 0 ? "" : " · 已停止";
        var notRun = run.NotRun == 0 ? "" : $" · {run.NotRun} 未执行";
        return $"上次运行：{counts}{stopped}{notRun}";
    }

    internal static string AccessibleCaption(RunProgressView run) =>
        $"已处理 {run.Processed} / {run.Total}，成功 {run.Succeeded}，失败 {run.Failed}";

    internal static string StateText(PlanItemProgress state) => state switch {
        PlanItemProgress.Pending => "等待中",
        PlanItemProgress.Running => "运行中",
        PlanItemProgress.Succeeded => "成功",
        PlanItemProgress.Failed => "失败",
        PlanItemProgress.Stopped => "已停止",
        _ => "未执行"
    };

    internal static string FormatDuration(TimeSpan duration)
    {
        var whole = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(duration.TotalSeconds)));
        return whole.TotalHours >= 1 ? $"{(int)whole.TotalHours}:{whole:mm\\:ss}" : whole.ToString("mm\\:ss");
    }

    // Elapsed time of a span that may still be open; an unstarted span has none.
    internal static TimeSpan? Elapsed(DateTime? startedAtUtc, DateTime? endedAtUtc, DateTime nowUtc) =>
        startedAtUtc is { } start ? (endedAtUtc ?? nowUtc) - start : null;

    private static RunProgressView Create(RunSnapshot run, bool active)
    {
        var items = run.Items.Select(item => new PlanItemProgressView(
            item.PlanItemId, item.TaskName, item.TaskLabel, Map(item), item.StartedAtUtc, item.EndedAtUtc)).ToArray();
        var current = active && run.CurrentPlanItemIndex is { } index && index >= 0 && index < items.Length
            ? (int?)index : null;
        return new RunProgressView(active, items, current, run.StartedAtUtc, run.EndedAtUtc);
    }
}
