using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Point = System.Windows.Point;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    // Each Run state maps to the item states, counts and caption the console shows; failures never count as success
    // and stopped or skipped items never count as processed.
    private static void VerifyRunProgressScenarios()
    {
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var start = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);
        RunSnapshot Run(RunState state, params (PlanItemState State, bool Started)[] items)
        {
            var planItems = items.Select((_, index) => new RunPlanItem(
                Guid.NewGuid(), $"Task{index}", $"任务 {index + 1}", "Entry", empty, empty)).ToArray();
            var plan = new RunPlan(1, start, new ProjectProvenance("self-test", "1", 1, "self-test"), "self-test",
                empty, planItems);
            var snapshots = planItems.Select((item, index) => new PlanItemSnapshot(item.PlanItemId, item.TaskName,
                item.TaskLabel, item.Entry, empty, empty, items[index].State,
                items[index].Started ? start.AddMinutes(index) : null, null, null, null, null)).ToArray();
            var current = Array.FindIndex(snapshots, item =>
                item.State is PlanItemState.Starting or PlanItemState.Running);
            return new RunSnapshot(Guid.NewGuid(), "digest", state, start, start, null, null,
                current < 0 ? null : snapshots[current].PlanItemId, current < 0 ? null : current, plan,
                snapshots, null, null);
        }
        var record = new WorkerAdmissionRecord(Guid.NewGuid(), "token", 1, Environment.ProcessId, "self-test",
            DateTime.UtcNow);
        var idle = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
        WorkerSnapshot Active(RunSnapshot run) => idle with { RunState = run.State, ActiveRun = run };
        WorkerSnapshot Last(RunSnapshot run) => idle with { LastRun = run };
        const PlanItemState pending = PlanItemState.Pending, running = PlanItemState.Running;
        const PlanItemState succeeded = PlanItemState.Succeeded, failed = PlanItemState.Failed;
        const PlanItemState cancelled = PlanItemState.Cancelled;
        string[] plan = ["Task0", "Task1", "Task2"];

        foreach (var (name, worker, tasks, states, current, caption) in new (string, WorkerSnapshot, string[],
            string, int?, string)[] {
            ("运行中首项", Active(Run(RunState.Running, (PlanItemState.Starting, true), (pending, false),
                (pending, false))), plan, "Running,Pending,Pending", 0, "已处理 0 / 3 · 0 成功"),
            ("运行中中间项", Active(Run(RunState.Running, (succeeded, true), (running, true), (pending, false))),
                plan, "Succeeded,Running,Pending", 1, "已处理 1 / 3 · 1 成功"),
            ("失败后", Active(Run(RunState.Running, (failed, true), (cancelled, false), (cancelled, false))),
                plan, "Failed,NotRun,NotRun", null, "已处理 1 / 3 · 0 成功 · 1 失败"),
            ("用户停止", Last(Run(RunState.Cancelled, (succeeded, true), (cancelled, true), (cancelled, false))),
                plan, "Succeeded,Stopped,NotRun", null, "上次运行：1 成功 · 已停止 · 1 未执行"),
            ("因失败中止", Last(Run(RunState.Failed, (succeeded, true), (failed, true), (cancelled, false))),
                plan, "Succeeded,Failed,NotRun", null, "上次运行：1 成功 · 1 失败 · 1 未执行"),
            ("全部成功", Last(Run(RunState.Succeeded, (succeeded, true), (succeeded, true), (succeeded, true))),
                plan, "Succeeded,Succeeded,Succeeded", null, "上次运行：3 成功")
        }) {
            var progress = RunProgress.Derive(worker, tasks);
            Require(progress is not null && string.Join(",", progress.Items.Select(item => item.State)) == states
                && progress.CurrentIndex == current && RunProgress.Caption(progress) == caption
                && progress.Processed == progress.Items.Count(item => item.State is PlanItemProgress.Succeeded
                    or PlanItemProgress.Failed)
                && progress.Succeeded == progress.Items.Count(item => item.State == PlanItemProgress.Succeeded),
                $"Run 进度场景“{name}”不符：{(progress is null ? "无" : RunProgress.Caption(progress))}。");
        }

        var stopped = RunProgress.Derive(Last(Run(RunState.Cancelled, (succeeded, true), (cancelled, true),
            (cancelled, false))), plan)!;
        Require(stopped.Processed == 1 && stopped.Stopped == 1 && stopped.NotRun == 1
            && RunProgress.AccessibleCaption(stopped) == "已处理 1 / 3，成功 1，失败 0",
            "已停止和未执行不计入已处理。");
        Require(RunProgress.Derive(Last(Run(RunState.Succeeded, (succeeded, true), (succeeded, true),
                (succeeded, true))), ["Task0", "Task2"]) is null && RunProgress.Derive(idle, plan) is null,
            "Last Run 计划与当前配置的任务序列不一致或没有 Run 时不显示进度。");
        Require(RunProgress.FormatDuration(TimeSpan.FromSeconds(65.9)) == "01:05"
            && RunProgress.FormatDuration(TimeSpan.FromMinutes(61)) == "1:01:00", "用时格式不符。");
    }

    // The console is one card with three sections; collapsing the preview gives its height to the activity log.
    private static void VerifyRuntimeConsole(AppLogger logger, string testDirectory)
    {
        Require(MainWindow.SidebarWidth(600) == 340 && MainWindow.SidebarWidth(1200) == 420
            && MainWindow.SidebarWidth(2000) == 540, "侧栏宽度应为主页内容的 35%，限制在 340–540。");
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "runtime-console"), (window, _) => {
            typeof(MainWindow).GetMethod("EndOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { true });
            var console = Named<FrameworkElement>(window, "RuntimeSidebar");
            var home = Named<FrameworkElement>(window, "HomeView");
            var log = Named<FrameworkElement>(window, "HomeLogListBox");
            Require(new[] { "RuntimeControlCard", "PreviewCard", "HomeLogListBox", "RuntimeMoreButton" }
                .All(name => console.IsAncestorOf(Named<Visual>(window, name))), "运行控制台应包含三个分区。");
            foreach (var width in new[] { 920, 1180, 1440 }) {
                window.Width = width;
                window.Height = width == 920 ? 640 : width == 1180 ? 760 : 900;
                PumpOnboarding();
                Require(Math.Abs(console.ActualWidth - MainWindow.SidebarWidth(home.ActualWidth)) < 1
                    && console.ActualWidth is >= 340 and <= 540
                    && Named<FrameworkElement>(window, "TasksView").ActualWidth > 0
                    && console.TransformToAncestor(home).Transform(new Point(console.ActualWidth, 0)).X
                        <= home.ActualWidth + 0.5, $"{width} 宽度下侧栏宽度或主页布局不符。");
            }
            var before = log.ActualHeight;
            void SetPreviewExpanded(bool expanded) => typeof(MainWindow).GetMethod("SetPreviewExpanded",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { expanded });
            SetPreviewExpanded(false);
            PumpOnboarding();
            Require(log.ActualHeight > before + 100, "收起游戏画面后运行动态应填满剩余高度。");
            SetPreviewExpanded(true);

            var desktop = new DesktopToggle(DesktopAction.Show, true);
            foreach (var (state, desktopReason, previewReason, endReason) in new (RuntimeControlState, string, string,
                string)[] {
                (new(new RuntimeAction.Start(true), true, null, desktop, true, Guid.NewGuid()), "", "", ""),
                (new(new RuntimeAction.Stop(true), false, LockReason.RunActive, desktop, true, null), "", "暂无画面", ""),
                (new(new RuntimeAction.Prepare(true), true, null, desktop with { Enabled = false }, true, null),
                    "未就绪", "暂无画面", "未创建"),
                (new(new RuntimeAction.InProgress(ProgressKind.PreparingEnvironment), false, LockReason.RuntimeBusy,
                    desktop with { Enabled = false }, false, null), "未就绪", "暂无画面", "处理中"),
                (new(new RuntimeAction.Recover(true, RecoveryReason.WorkerLost), false, LockReason.RuntimeBusy,
                    desktop, true, null), "", "暂无画面", "")
            }) {
                window.ApplyRuntimeControls(state);
                var items = new[] { "DesktopMenuItem", "ExpandPreviewMenuItem", "EndEnvironmentMenuItem" }
                    .Select(name => Named<System.Windows.Controls.MenuItem>(window, name)).ToArray();
                Require(items.Zip(new[] { desktopReason, previewReason, endReason }).All(pair =>
                        pair.First.IsEnabled == (pair.Second.Length == 0) && pair.First.InputGestureText == pair.Second)
                    && Named<System.Windows.Controls.MenuItem>(window, "OpenLogsMenuItem").IsEnabled,
                    $"{state.Action} 时更多操作菜单的可用性或原因不符。");
            }

            foreach (var (action, pill, title, detail) in new (RuntimeAction, string, string, string)[] {
                (new RuntimeAction.Prepare(true), "未准备", "开始任务前需要准备运行环境",
                    "将准备桌面分身、Worker 和游戏"),
                (new RuntimeAction.Start(false, StartBlocker.NoTasks), "就绪", "1 项任务待执行",
                    "请先在执行计划中添加任务"),
                (new RuntimeAction.InProgress(ProgressKind.StartingRun), "启动中", "正在开始任务",
                    "正在把执行计划交给 Worker…"),
                (new RuntimeAction.InProgress(ProgressKind.StoppingRun), "停止中", "正在停止任务",
                    "等待当前任务安全结束…"),
                (new RuntimeAction.Recover(true, RecoveryReason.WorkerLost), "故障", "Worker 失联",
                    "Worker 失联且未确认已退出；也可以等待它重新连接")
            }) {
                window.ApplyRuntimeControls(new(action, false, LockReason.RuntimeBusy, desktop, true, null));
                Require(Named<TextBlock>(window, "RuntimeStatusText").Text == pill
                    && Named<TextBlock>(window, "RuntimeStatusTitle").Text == title
                    && Named<TextBlock>(window, "RuntimeStatusDetail").Text == detail,
                    $"{action} 时状态胶囊、标题或说明不符。");
            }
            SetOnboardingField(window, "_sessionSnapshot",
                new ChildSessionSnapshot(ChildSessionState.ConnectedHidden, 7, 1, "fixture"));
            foreach (var (observation, fresh, detail) in new[] {
                (WorkerObservation.WorkerNotStarted, false, "桌面分身已连接，等待 Worker 启动…"),
                (WorkerObservation.WorkerStarting, false, "正在启动 Worker…"),
                (WorkerObservation.Connected, false, "正在同步 Worker 状态…"),
                (WorkerObservation.Connected, true, "正在完成运行环境准备…")
            }) {
                SetOnboardingField(window, "_workerSnapshot",
                    new WorkerCoordinatorSnapshot(observation, fresh, null, ""));
                window.ApplyRuntimeControls(new(new RuntimeAction.InProgress(ProgressKind.PreparingEnvironment),
                    false, LockReason.RuntimeBusy, desktop, false, null));
                Require(Named<TextBlock>(window, "RuntimeStatusDetail").Text == detail,
                    "准备阶段只能描述已观察到的 Session 和 Worker 状态。");
            }

            var placeholder = Named<ContentControl>(window, "HomePreviewPlaceholder");
            foreach (var (frame, title) in new (PreviewFrameInfo?, string)[] {
                (new(PreviewState.WaitingForWindow, 0, 0, DateTime.UtcNow, 0, 0), "正在连接画面…"),
                (null, "画面已中断"),
                (new(PreviewState.Unavailable, 0, 0, DateTime.UtcNow, 0, 0), "画面已中断")
            }) {
                typeof(MainWindow).GetMethod("PresentPreviewFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object?[] { frame, Array.Empty<byte>() });
                Require(Equals(placeholder.Tag, title), "Preview 传输中断与等待画面必须显示不同的空状态。");
            }
        });
    }

    // Card states, the progress caption and timing follow the Worker snapshot; the timer only rewrites text.
    private static void VerifyRunProgressWiring(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "run-progress-wiring"), (window, _) => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetMethod("EndOnboarding", flags)!.Invoke(window, new object[] { true });
            var empty = JsonDocument.Parse("{}").RootElement.Clone();
            var started = DateTime.UtcNow.AddSeconds(-65);
            var item = new RunPlanItem(Guid.NewGuid(), "RealTask", "Real task", "Entry", empty, empty);
            var plan = new RunPlan(1, started, new ProjectProvenance("self-test", "1", 1, "self-test"), "self-test",
                empty, [item]);
            PlanItemSnapshot Item(PlanItemState state, DateTime? ended) => new(item.PlanItemId, item.TaskName,
                item.TaskLabel, item.Entry, empty, empty, state, started, ended, null, null, null);
            var run = new RunSnapshot(Guid.NewGuid(), "digest", RunState.Running, started, started, null, null,
                item.PlanItemId, 0, plan, [Item(PlanItemState.Running, null)], null, null);
            var record = new WorkerAdmissionRecord(Guid.NewGuid(), "token", 1, Environment.ProcessId, "self-test",
                DateTime.UtcNow);
            var idle = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
            void Observe(WorkerSnapshot worker)
            {
                SetOnboardingField(window, "_workerSnapshot",
                    new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, worker, ""));
                InvokeOnboarding(window, "RefreshRuntimeControls");
                PumpOnboarding();
            }
            var timer = (System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_runTimer", flags)!
                .GetValue(window)!;
            var items = Named<System.Windows.Controls.Panel>(window, "PlanItemsPanel");
            string CardStatus() => ConfigurationDescendants(items).OfType<TextBlock>()
                .Single(text => text.Margin == new Thickness(4, 0, 6, 0)).Text;

            Observe(idle with { RunState = RunState.Running, ActiveRun = run });
            var card = items.Children.OfType<System.Windows.Controls.Border>().Single(border => border.Tag is not null);
            Require(CardStatus().StartsWith("运行中 · 01:0", StringComparison.Ordinal) && timer.IsEnabled
                && Named<TextBlock>(window, "RunProgressCaption").Text == "已处理 0 / 1 · 0 成功"
                && Named<TextBlock>(window, "RunProgressElapsed").Text.StartsWith("总用时 01:0", StringComparison.Ordinal)
                && Named<UIElement>(window, "RunProgressPanel").IsVisible,
                "运行中卡片应显示状态与用时，控制台应显示进度说明。");
            Require(Named<TextBlock>(window, "RuntimeStatusTitle").Text == "第 1 / 1 项：Real task",
                "运行状态应显示 Worker 当前项的序号与标签。");
            InvokeOnboarding(window, "RefreshRunTiming");
            Require(ReferenceEquals(card, items.Children.OfType<System.Windows.Controls.Border>()
                .Single(border => border.Tag is not null)), "计时刷新不得重建执行计划卡片。");

            Observe(idle with {
                LastRun = run with { State = RunState.Succeeded, Items = [Item(PlanItemState.Succeeded, started)] }
            });
            Require(CardStatus() == "成功" && !timer.IsEnabled, "Run 结束后卡片应显示结果，计时停止。");
            window.ApplyRuntimeControls(new(new RuntimeAction.Prepare(true), true, null,
                new DesktopToggle(DesktopAction.Show, false), true, null));
            Require(Named<UIElement>(window, "RunProgressPanel").IsVisible
                && Named<TextBlock>(window, "RunProgressCaption").Text == "上次运行：1 成功",
                "匹配当前配置的 Last Run 不能因运行环境需要重新准备而隐藏。");
        });
    }
}
