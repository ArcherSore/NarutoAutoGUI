using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;
using WpfButton = System.Windows.Controls.Button;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private const uint ScenarioSessionId = 7;
    private static readonly Guid ScenarioWorkerId = new("7a0c0e3e-5d1f-4b8e-9a51-000000000007");
    private static readonly string[] RuntimeHeaderControls = [
        "PrepareEnvironmentButton", "RetryEnvironmentButton", "StartTaskHeaderButton", "StopTaskHeaderButton",
        "RuntimeHeaderProgressRing"
    ];

    // The window shows what a RuntimeControlState says, independent of how the state was derived.
    private static void VerifyRuntimeControlMapping(AppLogger logger, string testDirectory)
    {
        WithRuntimeControlWindow(logger, Path.Combine(testDirectory, "runtime-mapping"), window => {
            var idleDesktop = new DesktopToggle(DesktopAction.Show, true);
            foreach (var (action, control, enabled, progress) in new (RuntimeAction, string, bool, string?)[] {
                (new RuntimeAction.Prepare(true), "PrepareEnvironmentButton", true, null),
                (new RuntimeAction.Prepare(false), "PrepareEnvironmentButton", false, null),
                (new RuntimeAction.Retry(RetryTarget.Prepare, true), "RetryEnvironmentButton", true, null),
                (new RuntimeAction.Retry(RetryTarget.Start, false), "RetryEnvironmentButton", false, null),
                (new RuntimeAction.Start(true), "StartTaskHeaderButton", true, null),
                (new RuntimeAction.Start(false), "StartTaskHeaderButton", false, null),
                (new RuntimeAction.Stop(true), "StopTaskHeaderButton", true, null),
                (new RuntimeAction.Stop(false), "StopTaskHeaderButton", false, null),
                (new RuntimeAction.InProgress(ProgressKind.PreparingEnvironment), "RuntimeHeaderProgressRing", true,
                    "正在准备运行环境"),
                (new RuntimeAction.InProgress(ProgressKind.StartingRun), "RuntimeHeaderProgressRing", true,
                    "正在开始任务"),
                (new RuntimeAction.InProgress(ProgressKind.StoppingRun), "RuntimeHeaderProgressRing", true,
                    "正在停止任务")
            }) {
                window.ApplyRuntimeControls(new RuntimeControlState(action, true, null, idleDesktop, true, null));
                var shown = ShownHeaderControls(window);
                Require(shown.SequenceEqual([control]), $"{action} 应只显示 {control}，实际为 {string.Join("、", shown)}。");
                var element = Named<FrameworkElement>(window, control);
                Require(progress is null ? element.IsEnabled == enabled
                    : Equals(element.ToolTip, progress) && AutomationProperties.GetName(element) == progress,
                    $"{action} 的可用性或进度说明不符。");
            }

            // A disabled Start or Retry-to-Start explains itself on hover and to screen readers; enabled ones keep
            // their usual tooltip.
            foreach (var (action, control, toolTip, helpText) in new (RuntimeAction, string, string, string)[] {
                (new RuntimeAction.Start(true), "StartTaskHeaderButton", "开始任务", ""),
                (new RuntimeAction.Start(false, StartBlocker.CommandsUnavailable), "StartTaskHeaderButton",
                    "当前操作完成后可开始任务", "当前操作完成后可开始任务"),
                (new RuntimeAction.Start(false, StartBlocker.ProjectNotLoaded), "StartTaskHeaderButton",
                    "MaaNOP 项目未加载，无法开始任务", "MaaNOP 项目未加载，无法开始任务"),
                (new RuntimeAction.Start(false, StartBlocker.NoTasks), "StartTaskHeaderButton",
                    "请先在执行计划中添加任务", "请先在执行计划中添加任务"),
                (new RuntimeAction.Start(false, StartBlocker.ConfigurationInvalid), "StartTaskHeaderButton",
                    "当前配置未通过校验，请先修正任务参数", "当前配置未通过校验，请先修正任务参数"),
                (new RuntimeAction.Start(false, StartBlocker.RuntimeNotReady), "StartTaskHeaderButton",
                    "运行环境尚未就绪，请稍候", "运行环境尚未就绪，请稍候"),
                (new RuntimeAction.Retry(RetryTarget.Prepare, true), "RetryEnvironmentButton", "重试", ""),
                (new RuntimeAction.Retry(RetryTarget.Start, true), "RetryEnvironmentButton", "重试", ""),
                (new RuntimeAction.Retry(RetryTarget.Start, false, StartBlocker.NoTasks), "RetryEnvironmentButton",
                    "请先在执行计划中添加任务", "请先在执行计划中添加任务")
            }) {
                window.ApplyRuntimeControls(new RuntimeControlState(action, true, null, idleDesktop, true, null));
                var button = Named<FrameworkElement>(window, control);
                Require(Equals(button.ToolTip, toolTip) && ToolTipService.GetShowOnDisabled(button)
                    && AutomationProperties.GetHelpText(button) == helpText, $"{action} 的说明不符。");
            }

            var state = new RuntimeControlState(new RuntimeAction.Prepare(true), true, null, idleDesktop, true, null);
            foreach (var (editable, lockReason, text) in new (bool, LockReason?, string?)[] {
                (false, LockReason.RunActive, "任务运行中，配置已锁定"),
                (false, LockReason.RuntimeBusy, "运行环境处理中，配置暂时锁定"),
                (false, null, null),
                (true, null, null)
            }) {
                window.ApplyRuntimeControls(
                    state with { ConfigurationEditable = editable, ConfigurationLock = lockReason });
                Require(Named<UIElement>(window, "NewConfigurationButton").IsEnabled == editable
                    && Named<UIElement>(window, "ConfigurationLockBadge").IsVisible == text is not null
                    && (text is null || Named<TextBlock>(window, "ConfigurationLockText").Text == text),
                    $"配置可编辑={editable}、锁定原因={lockReason} 的显示不符。");
            }

            foreach (var (desktop, text) in new (DesktopToggle, string)[] {
                (new DesktopToggle(DesktopAction.Hide, true), "隐藏分身"),
                (new DesktopToggle(DesktopAction.Show, false), "显示分身")
            }) {
                window.ApplyRuntimeControls(state with { Desktop = desktop });
                var button = Named<WpfButton>(window, "HomeDesktopVisibilityButton");
                Require(Named<AccessText>(window, "HomeDesktopVisibilityText").Text == text
                    && button.IsEnabled == desktop.Enabled && Equals(button.ToolTip, text)
                    && AutomationProperties.GetName(button) == text
                    && Equals(button.Tag, desktop.Action == DesktopAction.Hide ? "True" : "False"),
                    $"{desktop} 的分身按钮显示不符。");
            }
        });
        Console.WriteLine("RUNTIME CONTROL MAPPING PASS: header actions, configuration lock and desktop toggle.");
    }

    private static void VerifyRuntimeControlScenarios()
    {
        var scenarios = RuntimeControlScenarios();
        var failures = new List<string>();
        foreach (var (name, inputs, expected) in scenarios) {
            var actual = RuntimeControls.Derive(inputs);
            if (actual != expected) {
                failures.Add($"{name}{Environment.NewLine}  expected {expected}{Environment.NewLine}"
                    + $"  actual   {actual}");
            }
        }
        if (failures.Count != 0) {
            throw new InvalidOperationException(
                $"运行控制场景不符：{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        Console.WriteLine($"RUNTIME CONTROL SCENARIOS PASS: {scenarios.Length} scenarios.");
    }

    // Each row states the controls a user sees for one situation; expected values come from the rules in #12.
    private static (string Name, RuntimeControlInputs Inputs, RuntimeControlState Expected)[]
        RuntimeControlScenarios()
    {
        const string digest = "scenario-digest";
        var ready = new ProjectReadiness(true, 1, true, digest);
        var idle = ScenarioWorker(digest);
        var running = ScenarioWorker(digest, active: RunState.Running);
        var start = new RuntimeControlInputs(ScenarioSession(ChildSessionState.ConnectedHidden),
            Observed(WorkerObservation.Connected, idle), ready, PendingOperation.None, false, false);
        var stopped = start with {
            Session = ScenarioSession(ChildSessionState.NotRunning), Worker = WorkerCoordinatorSnapshot.Empty
        };
        var showEnabled = new DesktopToggle(DesktopAction.Show, true);
        var showDisabled = new DesktopToggle(DesktopAction.Show, false);
        var hideEnabled = new DesktopToggle(DesktopAction.Hide, true);
        Guid? worker = ScenarioWorkerId;
        Guid? none = null;
        var preparing = new RuntimeAction.InProgress(ProgressKind.PreparingEnvironment);
        var starting = new RuntimeAction.InProgress(ProgressKind.StartingRun);
        var stopping = new RuntimeAction.InProgress(ProgressKind.StoppingRun);
        var retryPrepare = new RuntimeAction.Retry(RetryTarget.Prepare, true);
        const LockReason runActive = LockReason.RunActive;
        const LockReason runtimeBusy = LockReason.RuntimeBusy;
        return [
            ("无分身冷启动", stopped,
                new(new RuntimeAction.Prepare(true), true, null, showDisabled, true, none)),
            ("恢复已有分身", stopped with {
                Session = ScenarioSession(ChildSessionState.Existing),
                PendingOperation = PendingOperation.RestoringSession
            }, new(preparing, false, runtimeBusy, showDisabled, false, none)),
            ("正在显示子桌面", start with { PendingOperation = PendingOperation.ShowingDesktop },
                new(BlockedStart(StartBlocker.CommandsUnavailable), false, runtimeBusy, showDisabled, false, worker)),
            ("正在创建分身", stopped with {
                Session = ScenarioSession(ChildSessionState.Connecting),
                PendingOperation = PendingOperation.PreparingEnvironment
            }, new(preparing, false, runtimeBusy, showDisabled, false, none)),
            ("分身已连接，正在启动 Worker", start with {
                Worker = Observed(WorkerObservation.WorkerStarting, null),
                PendingOperation = PendingOperation.PreparingEnvironment
            }, new(preparing, false, runtimeBusy, showDisabled, false, none)),
            ("Worker 已连接但仍在启动", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, WorkerState.Starting))
            }, new(preparing, true, null, showEnabled, true, none)),
            ("就绪，子桌面隐藏", start,
                new(new RuntimeAction.Start(true), true, null, showEnabled, true, worker)),
            ("就绪，子桌面可见", start with { Session = ScenarioSession(ChildSessionState.ConnectedVisible) },
                new(new RuntimeAction.Start(true), true, null, hideEnabled, true, worker)),
            ("准备失败，Worker 已就绪", start with { EnvironmentPreparationFailed = true },
                new(retryPrepare, true, null, showEnabled, true, none)),
            ("准备失败，分身故障", stopped with {
                Session = ScenarioSession(ChildSessionState.Faulted), EnvironmentPreparationFailed = true
            }, new(retryPrepare, true, null, showDisabled, true, none)),
            ("配置无效", start with { Project = ready with { ConfigurationValid = false } },
                new(BlockedStart(StartBlocker.ConfigurationInvalid), true, null, showEnabled, true, worker)),
            ("执行计划为空", start with { Project = ready with { SelectedTaskCount = 0 } },
                new(BlockedStart(StartBlocker.NoTasks), true, null, showEnabled, true, worker)),
            ("项目未加载", stopped with { Project = ProjectReadiness.NotLoaded },
                new(new RuntimeAction.Prepare(false), false, null, showDisabled, true, none)),
            ("Runtime Profile 不一致", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker("other-digest"))
            }, new(new RuntimeAction.Prepare(true), true, null, showEnabled, true, worker)),
            ("正在开始任务", start with { PendingOperation = PendingOperation.StartingRun },
                new(starting, false, runActive, showDisabled, false, worker)),
            ("Run 启动中", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, active: RunState.Starting))
            }, new(starting, false, runActive, showEnabled, true, worker)),
            ("运行中", start with { Worker = Observed(WorkerObservation.Connected, running) },
                new(new RuntimeAction.Stop(true), false, runActive, showEnabled, true, worker)),
            ("正在停止任务", start with {
                Worker = Observed(WorkerObservation.Connected, running), PendingOperation = PendingOperation.StoppingRun
            }, new(stopping, false, runActive, showDisabled, false, worker)),
            ("Run 停止中", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, active: RunState.Stopping))
            }, new(stopping, false, runActive, showEnabled, true, worker)),
            ("运行中 IPC 暂时断线", start with { Worker = Observed(WorkerObservation.IpcDisconnected, running) },
                new(new RuntimeAction.Stop(false), false, runActive, showEnabled, true, none)),
            ("空闲时 IPC 断线", start with { Worker = Observed(WorkerObservation.IpcDisconnected, idle) },
                new(retryPrepare, false, runtimeBusy, showEnabled, true, none)),
            ("快照断档等待刷新", start with {
                Worker = new WorkerCoordinatorSnapshot(WorkerObservation.Connected, false, idle, "scenario")
            }, new(new RuntimeAction.Prepare(true), false, runtimeBusy, showEnabled, true, none)),
            ("Worker 已退出", start with { Worker = Observed(WorkerObservation.WorkerExited, idle) },
                new(retryPrepare, false, runtimeBusy, showEnabled, true, none)),
            ("Worker 已退出，最后快照仍在运行", start with { Worker = Observed(WorkerObservation.WorkerExited, running) },
                new(retryPrepare, false, runtimeBusy, showEnabled, true, none)),
            ("Worker 恢复冲突", stopped with { Worker = Observed(WorkerObservation.WorkerRecoveryConflict, null) },
                new(retryPrepare, false, runtimeBusy, showDisabled, true, none)),
            ("分身已结束，最后快照仍在运行", stopped with {
                Worker = Observed(WorkerObservation.ChildSessionEnded, running)
            }, new(new RuntimeAction.Prepare(true), true, null, showDisabled, true, none)),
            ("分身已结束，最后快照仍在停止", stopped with {
                Worker = Observed(WorkerObservation.ChildSessionEnded,
                    ScenarioWorker(digest, active: RunState.Stopping))
            }, new(new RuntimeAction.Prepare(true), true, null, showDisabled, true, none)),
            ("上次 Run 失败", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, last: RunState.Failed))
            }, new(new RuntimeAction.Retry(RetryTarget.Start, true), true, null, showEnabled, true, worker)),
            ("上次 Run 失败且配置无效", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, last: RunState.Failed)),
                Project = ready with { ConfigurationValid = false }
            }, new(retryPrepare, true, null, showEnabled, true, worker)),
            ("上次 Run 失败且执行计划为空", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, last: RunState.Failed)),
                Project = ready with { SelectedTaskCount = 0 }
            }, new(retryPrepare, true, null, showEnabled, true, worker)),
            ("上次 Run 成功", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, last: RunState.Succeeded))
            }, new(new RuntimeAction.Start(true), true, null, showEnabled, true, worker)),
            ("Worker 故障", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, WorkerState.Faulted))
            }, new(retryPrepare, true, null, showEnabled, true, none)),
            ("Worker 依赖未就绪", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, WorkerState.NotReady))
            }, new(new RuntimeAction.Prepare(true), true, null, showEnabled, true, none)),
            ("退出中", start with { Exiting = true },
                new(BlockedStart(StartBlocker.CommandsUnavailable), false, runtimeBusy, showDisabled, false, none)),
            ("退出中且运行中", start with { Worker = Observed(WorkerObservation.Connected, running), Exiting = true },
                new(new RuntimeAction.Stop(false), false, runActive, showDisabled, false, none)),
            ("正在结束分身", start with { Session = ScenarioSession(ChildSessionState.Disconnecting) },
                new(new RuntimeAction.Prepare(false), false, runtimeBusy, showDisabled, false, none)),
            ("项目未加载但仍在运行", start with {
                Worker = Observed(WorkerObservation.Connected, running), Project = ProjectReadiness.NotLoaded
            }, new(new RuntimeAction.Stop(false), false, null, showEnabled, true, worker)),
            ("执行计划为空但仍在运行", start with {
                Worker = Observed(WorkerObservation.Connected, running), Project = ready with { SelectedTaskCount = 0 }
            }, new(new RuntimeAction.Stop(false), false, runActive, showEnabled, true, worker)),
            ("Worker 属于其他 Child Session", start with {
                Worker = Observed(WorkerObservation.Connected, ScenarioWorker(digest, childSessionId: 8))
            }, new(new RuntimeAction.Start(true), true, null, showEnabled, true, none))
        ];

        static WorkerCoordinatorSnapshot Observed(WorkerObservation observation, WorkerSnapshot? snapshot) =>
            new(observation, observation == WorkerObservation.Connected, snapshot, "scenario");

        static RuntimeAction.Start BlockedStart(StartBlocker blocker) => new(false, blocker);
    }

    private static ChildSessionSnapshot ScenarioSession(ChildSessionState state) => new(state,
        state == ChildSessionState.NotRunning ? null : ScenarioSessionId,
        state is ChildSessionState.ConnectedVisible or ChildSessionState.ConnectedHidden ? 1 : 0, "scenario");

    private static WorkerSnapshot ScenarioWorker(string digest, WorkerState state = WorkerState.Ready,
        RunState? active = null, RunState? last = null, uint childSessionId = ScenarioSessionId)
    {
        var empty = ProtocolJson.ToElement(new { });
        var itemId = new Guid("7a0c0e3e-5d1f-4b8e-9a51-0000000000a1");
        var plan = new RunPlan(ProtocolConstants.PlanVersion, DateTime.UnixEpoch,
            new ProjectProvenance("scenario", "1", 2, "scenario"), digest, empty,
            [new RunPlanItem(itemId, "Task", "Task", "Task", empty, empty)]);
        var activeItem = active == RunState.Starting ? PlanItemState.Starting : PlanItemState.Running;
        var lastItem = last switch {
            RunState.Succeeded => PlanItemState.Succeeded,
            RunState.Failed => PlanItemState.Failed,
            _ => PlanItemState.Cancelled
        };
        var available = new DependencyCheck(true, "scenario", null);
        return new WorkerSnapshot(ProtocolConstants.SnapshotVersion, DateTime.UnixEpoch, 1, ScenarioWorkerId,
            Environment.ProcessId, childSessionId, "scenario", ProtocolConstants.ProtocolVersion, digest,
            plan.Project, state, null, new DependencyStatus(DateTime.UnixEpoch, "scenario", "scenario",
                available, available, available, available, available),
            active ?? RunState.Idle, Run(active, activeItem), Run(last, lastItem), 0, 0);

        RunSnapshot? Run(RunState? runState, PlanItemState itemState) => runState is not { } value ? null
            : new RunSnapshot(Guid.NewGuid(), "scenario", value, DateTime.UnixEpoch, DateTime.UnixEpoch,
                null, null, itemId, 0, plan, [new PlanItemSnapshot(itemId, "Task", "Task", "Task", empty, empty,
                    itemState, DateTime.UnixEpoch, null, null, null, null)], null, null);
    }

    // Opens a real MainWindow off screen and lets its normal startup run; tests reach it only through its interface.
    private static void WithRuntimeControlWindow(AppLogger logger, string directory, Action<MainWindow> verify,
        ChildSessionManager? session = null, WorkerCoordinator? coordinator = null,
        Func<Func<Task>, Task>? runOperation = null, bool loadProject = true)
    {
        var projectDirectory = CreateProjectFixture(directory);
        if (!loadProject) {
            File.Delete(Path.Combine(projectDirectory, "interface.json"));
        }
        var configDirectory = Path.Combine(projectDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "update-check.txt"), "false");
        File.WriteAllText(Path.Combine(configDirectory, "onboarding.txt"), "1");
        using var ownedSession = session is null ? new SimulatedWts().CreateManager(logger) : null;
        var ownedCoordinator = coordinator is null
            ? new WorkerCoordinator(logger, Path.Combine(directory, "state"), "unused.exe",
                $"NarutoAutoGUI.RuntimeControls.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false)
            : null;
        var window = new MainWindow(logger, session ?? ownedSession!, new ChildSessionProgramService(logger),
            coordinator ?? ownedCoordinator!, runOperation ?? (operation => operation()), () => Task.CompletedTask,
            projectDirectory);
        var application = System.Windows.Application.Current;
        var shutdown = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try {
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            PumpOnboarding();
            verify(window);
        } finally {
            window.AllowClose();
            window.Close();
            PumpOnboarding();
            application.ShutdownMode = shutdown;
            if (ownedCoordinator is not null) {
                Task.Run(async () => await ownedCoordinator.DisposeAsync()).GetAwaiter().GetResult();
            }
        }
    }

    private static string[] ShownHeaderControls(MainWindow window) =>
        RuntimeHeaderControls.Where(name => Named<UIElement>(window, name).IsVisible).ToArray();

    private static T Named<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"MainWindow 缺少控件 {name}。");

    private static void Require(bool condition, string failure)
    {
        if (!condition) {
            throw new InvalidOperationException(failure);
        }
    }
}
