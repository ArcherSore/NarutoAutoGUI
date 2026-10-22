using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static partial class RunSelfTests
{
    private static RunPlan PretaskPlan(int tasks)
    {
        var plan = Plan(tasks + 1);
        var items = plan.Items.ToArray();
        items[0] = items[0] with {
            Kind = PlanItemKind.Pretask, Entry = "", Program = new PretaskCommand("launcher.exe", ["{}"])
        };
        return plan with { Items = items };
    }

    private static async Task VerifyPretaskProgressionAsync(RunHarness harness)
    {
        var request = Request(PretaskPlan(1));
        Expect(await harness.StartAsync(request) == "accepted", "Pretask + MaaTask 应被接受。 ");
        var first = await harness.Executions.ExecutingAsync(request.Plan.Items[0].PlanItemId);
        Expect(first.Item.Kind == PlanItemKind.Pretask
            && first.SnapshotAtExecute?.ActiveRun?.Items[0].Kind == PlanItemKind.Pretask,
            "Snapshot 必须保留 pretask Kind，且 pretask 首先执行。 ");
        Expect(await harness.StartAsync(request) == "already_accepted" && harness.Executions.Created.Count == 1,
            "重复提交同一 Run 不得重启 pretask。 ");
        first.ReportRunning();
        first.Complete(RuntimeExecutionOutcome.Succeeded);
        var next = await harness.Executions.ExecutingAsync(request.Plan.Items[1].PlanItemId);
        Expect(next.Item.Kind == PlanItemKind.MaaTask
            && next.SnapshotAtExecute?.ActiveRun?.Items[0].State == PlanItemState.Succeeded,
            "只有 pretask 成功后才能执行 MaaTask。 ");
        next.ReportRunning();
        next.Complete(RuntimeExecutionOutcome.Succeeded);
        var terminal = await TerminalAsync(harness, request.RunId);
        Expect(terminal.LastRun?.State == RunState.Succeeded, "混合队列应按现有规则成功终结。 ");
    }

    private static async Task VerifyPretaskOnlyAsync(RunHarness harness)
    {
        var request = Request(PretaskPlan(0));
        Expect(await harness.StartAsync(request) == "accepted", "只有 pretask 的计划也应被接受。 ");
        var first = await harness.Executions.ExecutingAsync(request.Plan.Items[0].PlanItemId);
        first.ReportRunning();
        first.Complete(RuntimeExecutionOutcome.Succeeded);
        var terminal = await TerminalAsync(harness, request.RunId);
        Expect(terminal.LastRun is { State: RunState.Succeeded, Items.Count: 1 }
            && terminal.LastRun.Items[0].Kind == PlanItemKind.Pretask,
            "仅 pretask 的 Run 不需要普通 task 即可完整终结。 ");
    }

    private static async Task VerifyPretaskFailureAsync(RunHarness harness)
    {
        var request = Request(PretaskPlan(1));
        Expect(await harness.StartAsync(request) == "accepted", "合法 pretask 应被接受。 ");
        var first = await harness.Executions.ExecutingAsync(request.Plan.Items[0].PlanItemId);
        first.ReportRunning();
        first.Complete(RuntimeExecutionOutcome.Failed, "PretaskFailed");
        var terminal = await TerminalAsync(harness, request.RunId);
        Expect(terminal.LastRun is { State: RunState.Failed, Error.Code: "PretaskFailed" }
            && terminal.LastRun.Items[1].Reason == "prior_item_failed"
            && harness.Executions.Created.Count == 1, "Pretask 失败必须阻止普通 task，不创建 controller 执行器。 ");
    }

    private static async Task VerifyPretaskValidationAsync(RunHarness harness)
    {
        var plan = PretaskPlan(1);
        var pretask = plan.Items[0];
        var task = plan.Items[1];
        RunPlanItem[] invalid = [
            pretask with { Program = null },
            pretask with { Entry = "Entry" },
            pretask with { Program = new PretaskCommand("", []) },
            pretask with { Program = new PretaskCommand("test.exe", ["bad\0value"]) },
            pretask with { Kind = (PlanItemKind)42 },
            task with { Program = pretask.Program }
        ];
        foreach (var item in invalid) {
            Expect(await harness.StartAsync(Request(plan with { Items = [item] })) == "error:invalid_run_plan",
                "非法 Kind/程序载荷必须在执行前拒绝。 ");
        }
        Expect(await harness.StartAsync(Request(plan with { Items = [task, pretask] })) == "error:invalid_run_plan",
            "普通 task 后不得执行 pretask。 ");
        Expect(harness.Executions.Created.Count == 0 && (await harness.SnapshotAsync()).StateRevision == 2,
            "非法前置计划不能启动程序或改变 Worker 状态。 ");
    }
}
