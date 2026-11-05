using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using static NarutoAutoGUI.Infrastructure.AppLogger;

namespace NarutoAutoGUI.Worker;

// Writes the Run results the Worker reports into the GUI log, once per GUI process. Snapshots can merge several changes
// and can arrive late, so each one is compared with what was already written instead of being read as one transition.
internal sealed class RunResultLog(AppLogger logger)
{
    private const int MaximumJsonLength = 2000;
    // Protocol JSON escapes non-ASCII text, which would leave Chinese option values unreadable in the log.
    private static readonly JsonSerializerOptions ReadableJson = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly object _gate = new();
    private readonly HashSet<Guid> _startedItems = [];
    private readonly HashSet<Guid> _endedItems = [];
    private readonly HashSet<Guid> _endedRuns = [];

    // What this Run was asked to do, written before it starts so a failure can be read against it.
    internal static void WritePlan(AppLogger logger, RunStartAttempt attempt, string configuration, bool startupOnly)
    {
        var items = attempt.Plan.Items;
        logger.Info($"Run 计划{Field("configuration", configuration)}{Field("startupOnly", startupOnly)}"
            + $"{Field("runId", attempt.RunId)}{Field("planDigest", attempt.PlanDigest)}{Field("items", items.Count)}"
            + Field("globalOptions", Options(attempt.Plan.ResolvedGlobalOptions)));
        for (var index = 0; index < items.Count; index++) {
            var item = items[index];
            logger.Info($"→ 计划项{Field("index", $"{index + 1}/{items.Count}")}{Field("task", item.TaskLabel)}"
                + $"{Field("name", item.TaskName)}{Field("kind", item.Kind)}{Field("entry", item.Entry)}"
                + Field("options", Options(item.ResolvedOptions)));
        }
    }

    private static string? Json(JsonElement? value) => value is { ValueKind: not JsonValueKind.Undefined } element
        ? JsonSerializer.Serialize(element, ReadableJson) : null;

    private static string? Options(JsonElement options)
    {
        var text = Json(options);
        return text switch {
            null or "{}" => null,
            { Length: > MaximumJsonLength } => $"{text[..MaximumJsonLength]}…（共 {text.Length} 字符）",
            _ => text
        };
    }

    internal void Observe(WorkerSnapshot? snapshot)
    {
        if (snapshot is null) {
            return;
        }
        // The caller is the Worker IPC read loop: a logging fault must not end the connection.
        try {
            lock (_gate) {
                foreach (var run in new[] { snapshot.LastRun, snapshot.ActiveRun }.OfType<RunSnapshot>()) {
                    ReportItems(run);
                    if (run.State is RunState.Succeeded or RunState.Failed or RunState.Cancelled
                        && _endedRuns.Add(run.RunId)) {
                        ReportRunEnd(run);
                    }
                }
            }
        } catch (Exception exception) {
            logger.Warn("写入 Run 结果日志失败。", exception);
        }
    }

    private void ReportItems(RunSnapshot run)
    {
        for (var index = 0; index < run.Items.Count; index++) {
            var item = run.Items[index];
            if (_endedItems.Contains(item.PlanItemId)) {
                continue;
            }
            if (item.State is PlanItemState.Starting or PlanItemState.Running) {
                if (_startedItems.Add(item.PlanItemId)) {
                    logger.Info($"→ 任务开始{Field("task", item.TaskLabel)}{Field("kind", item.Kind)}"
                        + $"{Field("entry", item.Entry)}{Field("index", $"{index + 1}/{run.Items.Count}")}");
                }
            } else if (item.State != PlanItemState.Pending && _endedItems.Add(item.PlanItemId)) {
                ReportItemEnd(item);
            }
        }
    }

    private void ReportItemEnd(PlanItemSnapshot item)
    {
        var details = $"{Field("task", item.TaskLabel)}{Field("state", item.State)}{Field("reason", item.Reason)}"
            + $"{Cost(item.StartedAtUtc, item.EndedAtUtc)}{Failure(item.Error, item.Result)}";
        if (item.StartedAtUtc is null) {
            logger.Info($"→ 任务未执行{details}");
        } else if (item.State == PlanItemState.Failed) {
            logger.Error($"→ 任务结束{details}");
        } else {
            logger.Info($"→ 任务结束{details}");
        }
    }

    private void ReportRunEnd(RunSnapshot run)
    {
        var failed = run.Items.FirstOrDefault(item => item.State == PlanItemState.Failed);
        var message = $"Run 结束{Field("state", run.State)}{Field("runId", run.RunId)}"
            + $"{Field("failedTask", failed?.TaskLabel)}{Failure(run.Error ?? failed?.Error, run.Result)}"
            + $"{Cost(run.StartedAtUtc, run.EndedAtUtc)}{Field("items", ItemCounts(run))}";
        if (run.State == RunState.Failed) {
            logger.Error(message);
        } else {
            logger.Info(message);
        }
    }

    private static string ItemCounts(RunSnapshot run) => string.Join(
        ",", run.Items.GroupBy(item => item.State).Select(group => $"{group.Key}:{group.Count()}"));

    private static string Failure(StructuredReason? error, JsonElement? result) =>
        $"{Field("code", error?.Code)}{Field("message", error?.Message.Trim())}{Field("result", Json(result))}";

    private static string Cost(DateTime? startedAtUtc, DateTime? endedAtUtc) =>
        startedAtUtc is { } started && endedAtUtc is { } ended
            ? Field("cost", ((ended - started).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + "s")
            : string.Empty;
}
