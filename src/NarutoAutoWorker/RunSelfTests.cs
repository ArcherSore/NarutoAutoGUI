using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

// Characterizes the Worker's Run rules through its IPC surface with scripted Plan Item executions (#16).
internal static class RunSelfTests
{
    private const int RaceIterations = 16;
    private static readonly TimeSpan StoppingWriteHold = TimeSpan.FromMilliseconds(300);

    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await ScenarioAsync("A1/C2/W3", VerifyAcceptedRunAsync);
        await ScenarioAsync("A2", VerifyRetryWhileActiveAsync);
        await ScenarioAsync("A3/A4/A10", VerifyRetryAfterTerminalAsync);
        await ScenarioAsync("A5/A6/A7/A8", VerifyLedgerPrecedesStateAsync);
        await ScenarioAsync("A7", VerifyStartingWorkerRejectsAsync, initialize: false);
        await ScenarioAsync("A7", VerifyNotReadyWorkerRejectsAsync, initialize: false);
        await ScenarioAsync("A9", VerifyPlanValidationAsync);
        await ScenarioAsync("A9", VerifySnapshotReserveAsync);
        await ScenarioAsync("R1-R4/C2", VerifyProgressionAsync);
        await ScenarioAsync("F1", VerifyFailureBeforeRunningAsync);
        await ScenarioAsync("F2", VerifyFailureMidPlanAsync);
        await ScenarioAsync("F3", VerifyCleanupFailureAsync);
        await ScenarioAsync("S1/S3/W1", VerifyStopRunningAsync);
        await ScenarioAsync("S2", VerifyStopStartingAsync);
        await ScenarioAsync("S4/S5", VerifyStopWinsEveryOutcomeAsync);
        await ScenarioAsync("S6", VerifyStopBeforeCompletionAsync);
        await ScenarioAsync("S7", VerifyCompletionBeforeStopAsync);
        await ScenarioAsync("S8", VerifyStopMismatchAsync);
        await ScenarioAsync("S9", VerifyStopFailureOnlyLogsAsync);
        await ScenarioAsync("T1/T2", VerifyStopTimedOutCurrentBehaviorAsync);
        await ScenarioAsync("C3", VerifyRunningRacesStopAsync);
        await ScenarioAsync("C4", VerifyCompletionRacesStopAsync);
        await ScenarioAsync("C5", VerifyConcurrentSnapshotsAsync);
        await ScenarioAsync("W2", VerifyStopSurvivesWriteFailureAsync);
        await ScenarioAsync("W5", VerifyPreviewNeedsReadyAsync, initialize: false);
    }

    // C1 applies to every scenario: no execution member may be called while the state lock is held.
    private static async Task ScenarioAsync(string id, Func<RunHarness, Task> body, bool initialize = true)
    {
        await using var harness = await RunHarness.CreateAsync(initialize);
        try {
            await body(harness);
        } catch (Exception exception) {
            throw new InvalidOperationException($"Run 特征测试 {id} 失败：{exception.Message}", exception);
        }
        var violations = harness.Executions.Violations;
        Expect(violations.Count == 0, $"Run 特征测试 {id}：执行方法在状态锁内被调用：{string.Join("，", violations)}");
    }

    private static async Task VerifyAcceptedRunAsync(RunHarness harness)
    {
        var before = await harness.SnapshotAsync();
        Expect(before is { StateRevision: 2, WorkerState: WorkerState.Ready, RunState: RunState.Idle },
            "就绪后应为 Ready、Idle，revision 为 2。");
        var request = Request(Plan(3));
        Expect(await harness.StartAsync(request) == "accepted", "合法计划应被接受。");

        var snapshot = await harness.SnapshotAsync();
        var run = snapshot.ActiveRun;
        Expect(snapshot.StateRevision == 3 && snapshot.RunState == RunState.Starting && snapshot.LastRun is null,
            "接受应为一次提交，Run 为 Starting 且没有 Last Run。");
        Expect(run is { State: RunState.Starting, StartedAtUtc: not null, CurrentPlanItemIndex: 0 }
            && run.CurrentPlanItemId == request.Plan.Items[0].PlanItemId && run.RunId == request.RunId,
            "Active Run 应从第一项开始。");
        Expect(run!.Items[0] is { State: PlanItemState.Starting, StartedAtUtc: not null }
            && run.Items.Skip(1).All(item => item is { State: PlanItemState.Pending, StartedAtUtc: null }),
            "第一项应为 Starting，其余为 Pending。");

        // W3: both the acknowledgement and the first execution happen; their order is not part of the contract.
        var first = await harness.Executions.ExecutingAsync(request.Plan.Items[0].PlanItemId);
        Expect(first.SnapshotAtExecute?.ActiveRun?.RunId == request.RunId
            && first.SnapshotAtExecute.ActiveRun.Items[0].State == PlanItemState.Starting,
            "首个 Plan Item 必须在接受提交可见后才开始执行。");
        Expect(harness.Executions.Created.Count == 1, "接受时只应创建第一项的执行。");
    }

    private static async Task VerifyRetryWhileActiveAsync(RunHarness harness)
    {
        var (request, _) = await StartRunningAsync(harness, 2, running: false);
        var revision = (await harness.SnapshotAsync()).StateRevision;
        Expect(await harness.StartAsync(request) == "already_accepted", "Active 期间重试应为 already_accepted。");
        Expect((await harness.SnapshotAsync()).StateRevision == revision && harness.Executions.Created.Count == 1,
            "重试不得提交状态或创建第二个执行。");
    }

    private static async Task VerifyRetryAfterTerminalAsync(RunHarness harness)
    {
        var (first, execution) = await StartRunningAsync(harness, 1);
        execution.Complete(RuntimeExecutionOutcome.Succeeded);
        await TerminalAsync(harness, first.RunId);
        var revision = (await harness.SnapshotAsync()).StateRevision;
        Expect(await harness.StartAsync(first) == "already_accepted", "Last Run 重试应为 already_accepted。");
        Expect((await harness.SnapshotAsync()).StateRevision == revision, "重试 Last Run 不得提交状态。");

        var (second, secondExecution) = await StartRunningAsync(harness, 1, running: false);
        var snapshot = await harness.SnapshotAsync();
        Expect(snapshot.ActiveRun?.RunId == second.RunId && snapshot.LastRun is null, "接受新 Run 应清空 Last Run。");
        Expect(await harness.StartAsync(first) == "already_accepted", "更早的 Run 重试应为 already_accepted。");
        secondExecution.Complete(RuntimeExecutionOutcome.Succeeded);
        await TerminalAsync(harness, second.RunId);
        Expect(await harness.StartAsync(first) == "already_accepted", "另一 Run 为 Last Run 时更早的 Run 仍应幂等。");
        Expect(harness.Executions.Created.Count == 2, "更早的 Run 重试不得再次执行。");
    }

    // Rejections follow the ledger, then Worker State, then the Active Run, then plan validation.
    private static async Task VerifyLedgerPrecedesStateAsync(RunHarness harness)
    {
        var (active, execution) = await StartRunningAsync(harness, 2);
        Expect(await harness.StartAsync(Request(Plan(1))) == "error:worker_busy", "已有 Active Run 时应为 worker_busy。");
        Expect(await harness.StartAsync(Request(Plan(0))) == "error:worker_busy", "Active Run 检查应先于计划校验。");
        var conflicting = Request(Plan(2), active.RunId);
        Expect(await harness.StartAsync(conflicting) == "error:run_id_conflict", "相同 runId 不同计划应为冲突。");

        execution.Complete(RuntimeExecutionOutcome.CleanupFailed, "AgentCleanupFailed");
        await TerminalAsync(harness, active.RunId);
        var faulted = await harness.SnapshotAsync();
        Expect(faulted.WorkerState == WorkerState.Faulted, "清理失败后 Worker 应为 Faulted。");
        Expect(await harness.StartAsync(active) == "already_accepted", "Faulted 时重试已接受的 Run 应为 already_accepted。");
        Expect(await harness.StartAsync(conflicting) == "error:run_id_conflict", "Faulted 时冲突仍先于状态检查。");
        Expect(await harness.StartAsync(Request(Plan(1))) == "error:worker_faulted", "Faulted 时新 Run 应被拒绝。");
        Expect(await harness.StartAsync(Request(Plan(0))) == "error:worker_faulted", "Worker State 检查应先于计划校验。");
        Expect((await harness.SnapshotAsync()).StateRevision == faulted.StateRevision, "拒绝不得提交状态。");
    }

    private static async Task VerifyStartingWorkerRejectsAsync(RunHarness harness)
    {
        Expect(await harness.StartAsync(Request(Plan(1))) == "error:operation_not_allowed",
            "初始化前应为 operation_not_allowed。");
        Expect((await harness.SnapshotAsync()) is { StateRevision: 1, WorkerState: WorkerState.Starting },
            "拒绝不得提交状态。");
    }

    private static async Task VerifyNotReadyWorkerRejectsAsync(RunHarness harness)
    {
        harness.ReadinessFailure = new StructuredReason("DependencyMissing", "scripted readiness failure");
        await harness.Host.InitializeAsync(CancellationToken.None);
        Expect(await harness.StartAsync(Request(Plan(1))) == "error:worker_not_ready",
            "NotReady 时应为 worker_not_ready。");
        Expect((await harness.SnapshotAsync()) is { StateRevision: 2, WorkerState: WorkerState.NotReady },
            "就绪检查失败应为 NotReady，拒绝不得提交状态。");
    }

    private static async Task VerifyPlanValidationAsync(RunHarness harness)
    {
        var valid = Plan(1);
        var cases = new (string Name, RunStartRequest Request, string Expected)[] {
            ("digest 格式", new RunStartRequest(Guid.NewGuid(), "bad", valid), "error:invalid_request"),
            ("planVersion", Request(valid with { PlanVersion = ProtocolConstants.PlanVersion + 1 }),
                "error:invalid_run_plan"),
            ("无 Plan Item", Request(Plan(0)), "error:invalid_run_plan"),
            ("重复 Plan Item ID", Request(valid with { Items = [valid.Items[0], valid.Items[0]] }),
                "error:invalid_run_plan"),
            ("Runtime Profile", Request(valid with { RuntimeProfileDigest = "sha256:" + new string('b', 64) }),
                "error:worker_not_ready"),
            ("digest 重算", new RunStartRequest(Guid.NewGuid(), Request(Plan(1)).PlanDigest, valid),
                "error:invalid_run_plan"),
            ("计划上限", Request(Plan(1, ProtocolConstants.MaximumRunPlanBytes)), "error:invalid_run_plan")
        };
        foreach (var (name, request, expected) in cases) {
            Expect(await harness.StartAsync(request) == expected, $"{name} 校验应返回 {expected}。");
        }
        Expect((await harness.SnapshotAsync()).StateRevision == 2 && harness.Executions.Created.Count == 0,
            "被拒绝的计划不得提交状态或执行。");

        var rejected = new RunStartRequest(Guid.NewGuid(), "bad", valid);
        await harness.StartAsync(rejected);
        Expect(await harness.StartAsync(Request(valid, rejected.RunId)) == "accepted",
            "被拒绝的请求不进入 ledger，修正后同一 runId 应被接受。");
    }

    // The reserve counts the retained Last Run, so a large previous Run can block a plan that fits on its own.
    private static async Task VerifySnapshotReserveAsync(RunHarness harness)
    {
        var (large, execution) = await StartRunningAsync(harness, 1, pipelineBytes: 900 * 1024);
        execution.Complete(RuntimeExecutionOutcome.Succeeded);
        await TerminalAsync(harness, large.RunId);
        var revision = (await harness.SnapshotAsync()).StateRevision;
        Expect(await harness.StartAsync(Request(Plan(1, 800 * 1024))) == "error:invalid_run_plan",
            "无法满足 Snapshot 终态预留时应拒绝。");
        Expect((await harness.SnapshotAsync()).StateRevision == revision, "预留不足的拒绝不得提交状态。");
    }

    private static async Task VerifyProgressionAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 3, running: false);
        var items = request.Plan.Items;
        first.ReportRunning();
        var running = await harness.SnapshotAsync();
        Expect(running.StateRevision == 4 && running.ActiveRun is { State: RunState.Running }
            && running.ActiveRun.Items[0].State == PlanItemState.Running, "onRunning 返回时应已提交 Running。");
        first.ReportRunning();
        Expect((await harness.SnapshotAsync()).StateRevision == 4, "重复 onRunning 应被忽略。");

        first.Complete(RuntimeExecutionOutcome.Succeeded);
        var second = await harness.Executions.ExecutingAsync(items[1].PlanItemId);
        var advanced = second.SnapshotAtExecute?.ActiveRun;
        Expect(advanced is { State: RunState.Running, CurrentPlanItemIndex: 1 }
            && advanced.Items[0] is { State: PlanItemState.Succeeded, EndedAtUtc: not null, Result: not null }
            && advanced.Items[1] is { State: PlanItemState.Starting, StartedAtUtc: not null }
            && advanced.Items[2].State == PlanItemState.Pending,
            "推进提交应在下一项开始执行前可见。");
        Expect((await harness.SnapshotAsync()).StateRevision == 5, "推进应为一次提交。");
        first.ReportRunning();
        Expect((await harness.SnapshotAsync()).StateRevision == 5, "上一项迟到的 onRunning 应被忽略。");

        second.ReportRunning();
        second.Complete(RuntimeExecutionOutcome.Succeeded);
        var third = await harness.Executions.ExecutingAsync(items[2].PlanItemId);
        third.ReportRunning();
        third.Complete(RuntimeExecutionOutcome.Succeeded);
        await TerminalAsync(harness, request.RunId);
        var done = await harness.SnapshotAsync();
        var last = done.LastRun!;
        Expect(done is { StateRevision: 9, RunState: RunState.Idle, ActiveRun: null, WorkerState: WorkerState.Ready,
            WorkerReason: null },
            "终结应为一次提交，Worker 回到 Ready。");
        Expect(last is { State: RunState.Succeeded, EndedAtUtc: not null, Error: null, CurrentPlanItemId: null,
            CurrentPlanItemIndex: null }
            && ScriptedItem(last.Result) == "Task3" && last.Items.All(item => item.State == PlanItemState.Succeeded),
            "Last Run 应为 Succeeded，结果来自最后一项。");
    }

    private static async Task VerifyFailureBeforeRunningAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 3, running: false);
        first.Complete(RuntimeExecutionOutcome.Failed, "RunExecutionFailed");
        await TerminalAsync(harness, request.RunId);
        var done = await harness.SnapshotAsync();
        var last = done.LastRun!;
        Expect(last is { State: RunState.Failed, Error.Code: "RunExecutionFailed" }
            && last.Items[0] is { State: PlanItemState.Failed, Error.Code: "RunExecutionFailed" }
            && last.Items.Skip(1).All(IsPriorItemFailed) && done.WorkerState == WorkerState.Ready,
            "Running 前失败应直接终结为 Failed，后续项以 prior_item_failed 取消。");
        Expect(harness.Executions.Created.Count == 1, "失败后不得执行后续项。");
    }

    private static async Task VerifyFailureMidPlanAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 3);
        first.Complete(RuntimeExecutionOutcome.Succeeded);
        var second = await harness.Executions.ExecutingAsync(request.Plan.Items[1].PlanItemId);
        second.ReportRunning();
        second.Complete(RuntimeExecutionOutcome.Failed, "MaaTaskFailed");
        await TerminalAsync(harness, request.RunId);
        var last = (await harness.SnapshotAsync()).LastRun!;
        Expect(last is { State: RunState.Failed, Error.Code: "MaaTaskFailed" }
            && last.Items[0].State == PlanItemState.Succeeded
            && last.Items[1] is { State: PlanItemState.Failed, Error.Code: "MaaTaskFailed" }
            && IsPriorItemFailed(last.Items[2]), "中间项失败应保留之前结果并取消之后项。");
        Expect(harness.Executions.Created.Count == 2, "失败后不得执行后续项。");
    }

    private static async Task VerifyCleanupFailureAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 2);
        first.Complete(RuntimeExecutionOutcome.CleanupFailed, "AgentCleanupFailed");
        await TerminalAsync(harness, request.RunId);
        var done = await harness.SnapshotAsync();
        Expect(done.LastRun is { State: RunState.Failed, Error.Code: "AgentCleanupFailed" }
            && IsPriorItemFailed(done.LastRun.Items[1])
            && done is { WorkerState: WorkerState.Faulted, WorkerReason.Code: "AgentCleanupFailed" },
            "未停止时清理失败应使 Run Failed、Worker Faulted。");
    }

    private static async Task VerifyStopRunningAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 3);
        var revision = (await harness.SnapshotAsync()).StateRevision;
        // W1 holds the Stopping event write for a while, so MaaFramework Stop started early from another thread is
        // caught too. Correct code waits out the hold, because Stop cannot start until that write completes.
        harness.Wire.HoldWrite = bytes => IsStoppingEvent(bytes)
            ? Task.WhenAny(first.StopCalled, Task.Delay(StoppingWriteHold)) : null;
        Expect(await harness.StopAsync(request.RunId) == "stop_requested", "Running 中停止应为 stop_requested。");
        var stopping = await harness.SnapshotAsync();
        var run = stopping.ActiveRun!;
        Expect(stopping.StateRevision == revision + 1 && stopping.RunState == RunState.Stopping
            && run is { State: RunState.Stopping, StopRequestedAtUtc: not null }
            && run.Items[0].State == PlanItemState.Running
            && run.Items.Skip(1).All(item => IsCancelled(item, "user_requested")),
            "停止应为一次提交：Run Stopping，Pending 项以 user_requested 取消，当前项不变。");

        // W1: the flag precedes the acknowledgement, which is fully written before the Stopping event, and
        // MaaFramework Stop starts only after that write. The GUI side never has to read for this to hold.
        await first.StopCalledAsync();
        Expect(first.WrittenAtRequestStop?.Any(IsStopResponse) == false, "RequestStop 必须先于 run.stop 响应写出。");
        var written = first.WrittenAtStop!.ToList();
        var response = written.FindIndex(IsStopResponse);
        var stoppingEvent = written.FindIndex(frame => frame.Operation == ProtocolOperations.RunStateChanged
            && RevisionOf(frame) == stopping.StateRevision);
        Expect(response >= 0 && stoppingEvent > response,
            "StopAsync 开始时，run.stop 响应及其后的 Stopping 事件应已完整写出。");

        Expect(await harness.StopAsync(request.RunId) == "already_stopping", "重复停止应为 already_stopping。");
        Expect((await harness.SnapshotAsync()).StateRevision == stopping.StateRevision
            && first is { RequestStops: 1, Stops: 1 }, "重复停止不得提交或再次请求停止。");

        first.Complete(RuntimeExecutionOutcome.Cancelled);
        await TerminalAsync(harness, request.RunId);
        var last = (await harness.SnapshotAsync()).LastRun!;
        Expect(last.State == RunState.Cancelled && IsCancelled(last.Items[0], "user_requested"),
            "停止后应终结为 Cancelled。");
    }

    private static async Task VerifyStopStartingAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 2, running: false);
        Expect(await harness.StopAsync(request.RunId) == "stop_requested", "Starting 中停止应为 stop_requested。");
        var revision = (await harness.SnapshotAsync()).StateRevision;
        first.ReportRunning();
        var stopping = await harness.SnapshotAsync();
        Expect(stopping.StateRevision == revision && stopping.ActiveRun!.Items[0].State == PlanItemState.Starting,
            "Stopping 后的 onRunning 应被忽略。");
        first.Complete(RuntimeExecutionOutcome.Cancelled);
        await TerminalAsync(harness, request.RunId);
        Expect(IsCancelled((await harness.SnapshotAsync()).LastRun!.Items[0], "user_requested"),
            "未开始运行的当前项应以 user_requested 取消。");
    }

    private static async Task VerifyStopWinsEveryOutcomeAsync(RunHarness harness)
    {
        var outcomes = new[] {
            RuntimeExecutionOutcome.Succeeded, RuntimeExecutionOutcome.Failed, RuntimeExecutionOutcome.Cancelled
        };
        foreach (var outcome in outcomes) {
            var (request, first) = await StartRunningAsync(harness, 2);
            Expect(await harness.StopAsync(request.RunId) == "stop_requested", "停止应被接受。");
            first.Complete(outcome, outcome == RuntimeExecutionOutcome.Failed ? "MaaTaskFailed" : null);
            await TerminalAsync(harness, request.RunId);
            var done = await harness.SnapshotAsync();
            var last = done.LastRun!;
            Expect(last is { State: RunState.Cancelled, Error: null } && IsCancelled(last.Items[0], "user_requested")
                && last.Items[0].Error is null && ScriptedItem(last.Result) == "Task1"
                && done is { WorkerState: WorkerState.Ready, WorkerReason: null },
                $"停止后结果为 {outcome} 时应终结为 Cancelled 且 Worker Ready。");
        }

        var (cleanup, execution) = await StartRunningAsync(harness, 1);
        await harness.StopAsync(cleanup.RunId);
        execution.Complete(RuntimeExecutionOutcome.CleanupFailed, "AgentCleanupFailed");
        await TerminalAsync(harness, cleanup.RunId);
        var faulted = await harness.SnapshotAsync();
        Expect(faulted.LastRun is { State: RunState.Cancelled, Error: null }
            && faulted is { WorkerState: WorkerState.Faulted, WorkerReason.Code: "AgentCleanupFailed" },
            "停止后清理失败应为 Cancelled 且只在 WorkerReason 中体现。");
    }

    private static async Task VerifyStopBeforeCompletionAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 2);
        Expect(await harness.StopAsync(request.RunId) == "stop_requested", "停止应被接受。");
        first.Complete(RuntimeExecutionOutcome.Succeeded);
        await TerminalAsync(harness, request.RunId);
        var last = (await harness.SnapshotAsync()).LastRun!;
        Expect(last.State == RunState.Cancelled && harness.Executions.Created.Count == 1
            && IsCancelled(last.Items[1], "user_requested"), "停止先被接受时下一项不得开始。");
    }

    private static async Task VerifyCompletionBeforeStopAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 3);
        first.Complete(RuntimeExecutionOutcome.Succeeded);
        var second = await harness.Executions.ExecutingAsync(request.Plan.Items[1].PlanItemId);
        Expect(await harness.StopAsync(request.RunId) == "stop_requested", "停止应被接受。");
        await second.StopCalledAsync();
        var stopping = await harness.SnapshotAsync();
        Expect(first.RequestStops == 0 && second is { RequestStops: 1, Stops: 1 },
            "推进后停止应作用于下一项。");
        Expect(stopping.ActiveRun is { State: RunState.Stopping } && stopping.LastRun is null
            && stopping.ActiveRun.Items[0].State == PlanItemState.Succeeded,
            "下一项返回结果前 Run 应保持 Stopping，前一项保持 Succeeded。");
        second.Complete(RuntimeExecutionOutcome.Cancelled);
        await TerminalAsync(harness, request.RunId);
        var last = (await harness.SnapshotAsync()).LastRun!;
        Expect(last.State == RunState.Cancelled && IsCancelled(last.Items[2], "user_requested"),
            "下一项返回结果后才终结为 Cancelled。");
    }

    private static async Task VerifyStopMismatchAsync(RunHarness harness)
    {
        Expect(await harness.StopAsync(Guid.NewGuid()) == "error:run_id_mismatch", "没有 Run 时应为 run_id_mismatch。");
        var (older, olderExecution) = await StartRunningAsync(harness, 1);
        olderExecution.Complete(RuntimeExecutionOutcome.Succeeded);
        await TerminalAsync(harness, older.RunId);
        Expect(await harness.StopAsync(older.RunId) == "already_terminal", "Last Run 应为 already_terminal。");

        var (active, activeExecution) = await StartRunningAsync(harness, 1);
        Expect(await harness.StopAsync(older.RunId) == "error:run_id_mismatch", "与 Active Run 不同应为 mismatch。");
        Expect(await harness.StopAsync(Guid.NewGuid()) == "error:run_id_mismatch", "未知 runId 应为 mismatch。");
        activeExecution.Complete(RuntimeExecutionOutcome.Succeeded);
        await TerminalAsync(harness, active.RunId);
        Expect(await harness.StopAsync(older.RunId) == "error:run_id_mismatch", "更早的 Run 应为 mismatch。");
    }

    private static async Task VerifyStopFailureOnlyLogsAsync(RunHarness harness)
    {
        harness.Executions.StopFails = true;
        var (request, first) = await StartRunningAsync(harness, 1);
        Expect(await harness.StopAsync(request.RunId) == "stop_requested", "停止应被接受。");
        var revision = (await harness.SnapshotAsync()).StateRevision;
        await first.StopCalledAsync();
        await harness.WaitForLogAsync(entry => entry is { Level: "ERROR", Source: "run.stop" }
            && entry.RunId == request.RunId, "Stop 失败的 ERROR 日志");
        var snapshot = await harness.SnapshotAsync();
        Expect(snapshot.StateRevision == revision && snapshot.ActiveRun?.State == RunState.Stopping,
            "StopAsync 失败本身不得改变状态。");
    }

    // Current behavior tracked by #17: an unconfirmed stop faults the Worker and leaves the Run in Stopping.
    private static async Task VerifyStopTimedOutCurrentBehaviorAsync(RunHarness harness)
    {
        harness.Executions.StopFails = true;
        var (request, first) = await StartRunningAsync(harness, 2);
        await harness.StopAsync(request.RunId);
        var revision = (await harness.SnapshotAsync()).StateRevision;
        await first.StopCalledAsync();
        first.Complete(RuntimeExecutionOutcome.StopTimedOut, "StopTimeout");
        await harness.WaitForStateAsync(state => state.WorkerState == WorkerState.Faulted, "StopTimedOut 后 Faulted");
        var faulted = await harness.SnapshotAsync();
        Expect(faulted is { WorkerState: WorkerState.Faulted, WorkerReason.Code: "StopTimeout", LastRun: null,
            RunState: RunState.Stopping }
            && faulted.StateRevision == revision + 1 && faulted.ActiveRun?.RunId == request.RunId,
            "#17 当前行为：Worker Faulted，Run 停留在 Stopping 且没有终态。");

        Expect(await harness.StartAsync(Request(Plan(1))) == "error:worker_faulted", "#17 当前行为：新 Run 被拒绝。");
        Expect(await harness.StartAsync(request) == "already_accepted", "#17 当前行为：同一 Run 仍按 ledger 幂等。");
        Expect(await harness.StopAsync(request.RunId) == "already_stopping", "#17 当前行为：再次停止为 already_stopping。");
        Expect((await harness.SnapshotAsync()).StateRevision == faulted.StateRevision, "#17 当前行为：上述请求不提交。");
    }

    // C3: either Running is committed first (two commits) or Stopping wins and Running is ignored (one commit).
    private static async Task VerifyRunningRacesStopAsync(RunHarness harness)
    {
        for (var iteration = 0; iteration < RaceIterations; iteration++) {
            var (request, first) = await StartRunningAsync(harness, 1, running: false);
            var revision = (await harness.SnapshotAsync()).StateRevision;
            var running = Task.Run(first.ReportRunning);
            var stop = await harness.StopAsync(request.RunId);
            await running;
            var snapshot = await harness.SnapshotAsync();
            var item = snapshot.ActiveRun!.Items[0].State;
            Expect(stop == "stop_requested" && snapshot.RunState == RunState.Stopping
                && (item == PlanItemState.Running && snapshot.StateRevision == revision + 2
                || item == PlanItemState.Starting && snapshot.StateRevision == revision + 1),
                $"onRunning 与停止竞争出现非法结果：item={item}，revision 增量={snapshot.StateRevision - revision}。");
            first.Complete(RuntimeExecutionOutcome.Cancelled);
            await TerminalAsync(harness, request.RunId);
        }
    }

    // C4: stop either lands on the current item, or completion wins and the stop lands on the next item; then the
    // Run stays Stopping until that item returns its result.
    private static async Task VerifyCompletionRacesStopAsync(RunHarness harness)
    {
        for (var iteration = 0; iteration < RaceIterations; iteration++) {
            var (request, first) = await StartRunningAsync(harness, 2);
            var completing = Task.Run(() => first.Complete(RuntimeExecutionOutcome.Succeeded));
            Expect(await harness.StopAsync(request.RunId) == "stop_requested", "竞争中的停止应被接受。");
            await completing;
            if (first.RequestStops == 1) {
                await TerminalAsync(harness, request.RunId);
                var next = request.Plan.Items[1].PlanItemId;
                Expect(!harness.Executions.Created.Any(execution => execution.Item.PlanItemId == next),
                    "停止先被接受时下一项不得创建。");
            } else {
                var second = await harness.Executions.ExecutingAsync(request.Plan.Items[1].PlanItemId);
                Expect(second.RequestStops == 1, "完成先胜出时停止应作用于下一项。");
                var stopping = await harness.SnapshotAsync();
                Expect(stopping.ActiveRun?.RunId == request.RunId && stopping.RunState == RunState.Stopping,
                    "完成先胜出时，下一项返回结果前 Run 应保持 Stopping。");
                second.Complete(RuntimeExecutionOutcome.Cancelled);
                await TerminalAsync(harness, request.RunId);
            }
            Expect((await harness.SnapshotAsync()).LastRun?.State == RunState.Cancelled, "竞争后应终结为 Cancelled。");
        }
    }

    // C5: a reader on another thread snapshots throughout each Run. Snapshots hold the state lock for a few
    // milliseconds, so the reader pauses between reads to let the Run progress meanwhile.
    private static async Task VerifyConcurrentSnapshotsAsync(RunHarness harness)
    {
        for (var iteration = 0; iteration < 3; iteration++) {
            var observed = new List<WorkerSnapshot>();
            using var done = new CancellationTokenSource();
            var reader = Task.Run(() =>
            {
                while (!done.IsCancellationRequested) {
                    observed.Add(harness.Host.GetSnapshot());
                    Thread.Sleep(1);
                }
            });
            var (request, execution) = await StartRunningAsync(harness, 5);
            foreach (var item in request.Plan.Items.Skip(1)) {
                execution.Complete(RuntimeExecutionOutcome.Succeeded);
                execution = await harness.Executions.ExecutingAsync(item.PlanItemId);
                execution.ReportRunning();
            }
            execution.Complete(RuntimeExecutionOutcome.Succeeded);
            await TerminalAsync(harness, request.RunId);
            done.Cancel();
            await reader;
            VerifyConsistentSnapshots(observed);
        }
    }

    private static void VerifyConsistentSnapshots(List<WorkerSnapshot> observed)
    {
        Expect(observed.Any(snapshot => snapshot.ActiveRun?.CurrentPlanItemIndex > 0),
            "并发读取应在 Run 推进期间取得 Snapshot。");
        for (var index = 1; index < observed.Count; index++) {
            Expect(observed[index].StateRevision >= observed[index - 1].StateRevision, "stateRevision 不得回退。");
        }
        foreach (var snapshot in observed) {
            Expect(snapshot.RunState == (snapshot.ActiveRun?.State ?? RunState.Idle), "RunState 应与 Active Run 一致。");
            if (snapshot.ActiveRun is { } run) {
                var current = run.Items.Select((item, index) => (item, index))
                    .Where(pair => pair.item.State is PlanItemState.Starting or PlanItemState.Running).ToArray();
                Expect(current.Length == 1 && current[0].index == run.CurrentPlanItemIndex,
                    "Active Run 应恰有一项处于 Starting/Running 且为当前项。");
            }
            Expect(snapshot.LastRun is null or { CurrentPlanItemIndex: null }, "终态 Run 不应有当前项。");
        }
    }

    // W2: an accepted stop still reaches MaaFramework when the Stopping event cannot be written.
    private static async Task VerifyStopSurvivesWriteFailureAsync(RunHarness harness)
    {
        var (request, first) = await StartRunningAsync(harness, 1);
        harness.Wire.FailWhen = frames => frames.Any(IsStopResponse);
        Expect(await harness.StopAsync(request.RunId) == "stop_requested", "停止应被接受。");
        await first.StopCalledAsync();
        try {
            await harness.Serving.WaitAsync(RunHarness.WaitLimit);
            throw new InvalidOperationException("Stopping 事件写出失败后连接应结束。");
        } catch (IOException) {
        }
        Expect(harness.Host.GetSnapshot().ActiveRun?.State == RunState.Stopping, "写出失败不得改变 Stopping 状态。");
    }

    private static async Task VerifyPreviewNeedsReadyAsync(RunHarness harness)
    {
        harness.ReadinessFailure = new StructuredReason("DependencyMissing", "scripted readiness failure");
        await harness.Host.InitializeAsync(CancellationToken.None);
        var worker = (await harness.SnapshotAsync()).WorkerInstanceId;
        var preview = new PreviewRequest(worker, Guid.NewGuid());
        foreach (var operation in new[] { ProtocolOperations.PreviewStart, ProtocolOperations.PreviewRenew }) {
            var response = await harness.RequestAsync(operation, preview);
            Expect(response is { Success: false, Error.Code: "worker_not_ready" }, $"NotReady 时 {operation} 应被拒绝。");
        }
        Expect((await harness.RequestAsync(ProtocolOperations.PreviewStop, preview)).Success == true,
            "NotReady 时 preview.stop 仍应可用。");
    }

    private static async Task<(RunStartRequest Request, ScriptedExecution First)> StartRunningAsync(
        RunHarness harness, int count, bool running = true, int pipelineBytes = 0)
    {
        var request = Request(Plan(count, pipelineBytes));
        Expect(await harness.StartAsync(request) == "accepted", "合法计划应被接受。");
        var first = await harness.Executions.ExecutingAsync(request.Plan.Items[0].PlanItemId);
        if (running) {
            first.ReportRunning();
        }
        return (request, first);
    }

    private static Task<WorkerSnapshot> TerminalAsync(RunHarness harness, Guid runId) =>
        harness.WaitForStateAsync(state => state.ActiveRun is null && state.LastRun?.RunId == runId, $"Run {runId} 终结");

    private static long RevisionOf(WireEnvelope stateEvent) =>
        ProtocolJson.Deserialize<StateChangedEvent>(stateEvent.Data).Snapshot.StateRevision;

    // Frames are written as a length prefix followed by the JSON payload; only the payload can match.
    private static bool IsStoppingEvent(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Span[0] != (byte)'{') {
            return false;
        }
        var frame = JsonSerializer.Deserialize<WireEnvelope>(bytes.Span, ProtocolJson.Options)!;
        return frame.Operation == ProtocolOperations.RunStateChanged
            && ProtocolJson.Deserialize<StateChangedEvent>(frame.Data).Snapshot.RunState == RunState.Stopping;
    }

    private static bool IsStopResponse(WireEnvelope frame) =>
        frame is { MessageType: ProtocolMessageTypes.Response, Operation: ProtocolOperations.RunStop, Success: true };

    private static bool IsCancelled(PlanItemSnapshot item, string reason) =>
        item.State == PlanItemState.Cancelled && item.Reason == reason && item.EndedAtUtc is not null;

    private static bool IsPriorItemFailed(PlanItemSnapshot item) => IsCancelled(item, "prior_item_failed");

    private static string? ScriptedItem(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("item", out var item)
            ? item.GetString() : null;

    // pipelineBytes pads the first Plan Item so budget rules can be reached.
    internal static RunPlan Plan(int count, int pipelineBytes = 0)
    {
        var empty = ProtocolJson.ToElement(new { });
        var padded = pipelineBytes == 0 ? empty : ProtocolJson.ToElement(new { pad = new string('x', pipelineBytes) });
        var items = Enumerable.Range(1, count).Select(index => new RunPlanItem(
            Guid.NewGuid(), $"Task{index}", $"任务{index}", $"Entry{index}", empty,
            index == 1 ? padded : empty)).ToArray();
        return new RunPlan(ProtocolConstants.PlanVersion, DateTime.UtcNow, RunHarness.Project,
            RunHarness.ProfileDigest, empty, items);
    }

    internal static RunStartRequest Request(RunPlan plan, Guid? runId = null) =>
        new(runId ?? Guid.NewGuid(), CanonicalDigest.ComputePlanDigestV1(plan), plan);

    internal static void Expect(bool condition, string message)
    {
        if (!condition) {
            throw new InvalidOperationException(message);
        }
    }
}
