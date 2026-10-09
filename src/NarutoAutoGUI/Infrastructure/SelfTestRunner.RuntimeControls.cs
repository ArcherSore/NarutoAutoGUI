using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private const uint ScenarioSessionId = 7;
    private static readonly Guid ScenarioWorkerId = new("7a0c0e3e-5d1f-4b8e-9a51-000000000007");

    private static void VerifyRuntimeControlScenarios(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "runtime-controls");
        var projectDirectory = CreateProjectFixture(directory);
        var withTask = ProjectPlanModule.Open(projectDirectory, Path.Combine(directory, "with-task.json"));
        var withoutTask = ProjectPlanModule.Open(projectDirectory, Path.Combine(directory, "without-task.json"));
        withoutTask.RemoveTask(withoutTask.SelectedTaskNames.Single());
        var scenarios = RuntimeControlScenarios(withTask.RuntimeProfileDigest);
        var failures = new List<string>();
        WithScenarioWindow(logger, directory, projectDirectory, window => {
            foreach (var (name, inputs, expected) in scenarios) {
                var actual = ObserveCurrentWindow(window, inputs, withTask, withoutTask);
                if (actual != expected) {
                    failures.Add($"{name}{Environment.NewLine}  expected {expected}{Environment.NewLine}"
                        + $"  actual   {actual}");
                }
            }
        });
        if (failures.Count != 0) {
            throw new InvalidOperationException(
                $"运行控制场景不符：{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        Console.WriteLine($"RUNTIME CONTROL SCENARIOS PASS: {scenarios.Length} scenarios.");
    }

    // Each row states the controls a user sees for one situation; expected values come from the rules in #12.
    private static (string Name, RuntimeControlInputs Inputs, RuntimeControlState Expected)[]
        RuntimeControlScenarios(string digest)
    {
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
                new(new RuntimeAction.Start(false), false, runtimeBusy, showDisabled, false, worker)),
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
                new(new RuntimeAction.Start(false), true, null, showEnabled, true, worker)),
            ("执行计划为空", start with { Project = ready with { SelectedTaskCount = 0 } },
                new(new RuntimeAction.Start(false), true, null, showEnabled, true, worker)),
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
                new(new RuntimeAction.Start(false), false, runtimeBusy, showDisabled, false, none)),
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

    private static void WithScenarioWindow(
        AppLogger logger, string directory, string projectDirectory, Action<MainWindow> verify)
    {
        var configDirectory = Path.Combine(projectDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "update-check.txt"), "false");
        File.WriteAllText(Path.Combine(configDirectory, "onboarding.txt"), "1");
        using var session = new SimulatedWts().CreateManager(logger);
        var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "state"), "unused.exe",
            $"NarutoAutoGUI.RuntimeControls.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
        var window = new MainWindow(logger, session, new ChildSessionProgramService(logger), coordinator,
            operation => operation(), () => Task.CompletedTask, projectDirectory);
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
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    // Characterizes the controls of the current MainWindow; it reads private members only until extraction.
    private static RuntimeControlState ObserveCurrentWindow(MainWindow window, RuntimeControlInputs inputs,
        ProjectPlanModule withTask, ProjectPlanModule withoutTask)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        var project = inputs.Project;
        if (project.Loaded && (project.SelectedTaskCount is not (0 or 1)
            || project.RuntimeProfileDigest != withTask.RuntimeProfileDigest)) {
            throw new InvalidOperationException($"场景项目状态无法由夹具表示：{project}。");
        }
        var status = inputs.PendingOperation switch {
            PendingOperation.None => string.Empty,
            PendingOperation.RestoringSession => "正在恢复已有桌面分身...",
            PendingOperation.ShowingDesktop => "正在显示子桌面...",
            PendingOperation.PreparingEnvironment => "正在准备运行环境...",
            PendingOperation.StartingRun => "正在开始任务...",
            _ => "正在停止任务..."
        };
        Set("_sessionSnapshot", inputs.Session);
        Set("_workerSnapshot", inputs.Worker);
        Set("_projectPlan", !project.Loaded ? null : project.SelectedTaskCount == 0 ? withoutTask : withTask);
        Set("_projectConfigurationValid", project.ConfigurationValid);
        Set("_busy", inputs.PendingOperation != PendingOperation.None);
        Set("_operationStatus", status);
        Set("_environmentPreparationFailed", inputs.EnvironmentPreparationFailed);
        Set("_exitInProgress", inputs.Exiting);
        type.GetMethod("UpdateCommandAvailability", flags)!.Invoke(window, null);

        var shown = new[] {
            "PrepareEnvironmentButton", "RetryEnvironmentButton", "StartTaskHeaderButton", "StopTaskHeaderButton",
            "RuntimeHeaderProgressRing"
        }.Where(name => Named(name).Visibility == Visibility.Visible).ToArray();
        if (shown.Length != 1) {
            throw new InvalidOperationException($"运行控制应恰好显示一个主操作，实际为 {string.Join("、", shown)}。");
        }
        var enabled = Named(shown[0]).IsEnabled;
        var retryTarget = inputs.EnvironmentPreparationFailed || !Get<bool>("IsRunReadyToStart")
            ? RetryTarget.Prepare : RetryTarget.Start;
        RuntimeAction action = shown[0] switch {
            "PrepareEnvironmentButton" => new RuntimeAction.Prepare(enabled),
            "RetryEnvironmentButton" => new RuntimeAction.Retry(retryTarget, enabled),
            "StartTaskHeaderButton" => new RuntimeAction.Start(enabled),
            "StopTaskHeaderButton" => new RuntimeAction.Stop(enabled),
            _ => new RuntimeAction.InProgress(((FrameworkElement)Named(shown[0])).ToolTip switch {
                "正在停止任务" => ProgressKind.StoppingRun,
                "正在开始任务" => ProgressKind.StartingRun,
                "正在准备运行环境" => ProgressKind.PreparingEnvironment,
                var other => throw new InvalidOperationException($"未知进度说明：{other}。")
            })
        };
        var editableProperty = (DependencyProperty)type
            .GetField("ConfigurationEditableProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var lockText = ((TextBlock)Named("ConfigurationLockText")).Text;
        LockReason? lockReason = Named("ConfigurationLockBadge").Visibility != Visibility.Visible ? null
            : lockText switch {
                "任务运行中，配置已锁定" => LockReason.RunActive,
                "运行环境处理中，配置暂时锁定" => LockReason.RuntimeBusy,
                _ => throw new InvalidOperationException($"未知配置锁定说明：{lockText}。")
            };
        var desktopButton = (System.Windows.Controls.Button)Named("HomeDesktopVisibilityButton");
        var desktopAction = ((AccessText)Named("HomeDesktopVisibilityText")).Text == "隐藏分身"
            ? DesktopAction.Hide : DesktopAction.Show;
        if (Equals(desktopButton.Tag, "True") != (desktopAction == DesktopAction.Hide)) {
            throw new InvalidOperationException("显示/隐藏分身按钮的状态标记与文字不一致。");
        }
        var previewArguments = new object?[] { null };
        var previewTarget = (bool)type.GetMethod("TryGetPreviewTarget", flags)!.Invoke(window, previewArguments)!;
        return new RuntimeControlState(action, (bool)window.GetValue(editableProperty), lockReason,
            new DesktopToggle(desktopAction, desktopButton.IsEnabled), Get<bool>("CanRunCommand"),
            previewTarget ? (Guid)previewArguments[0]! : null);

        void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(window, value);
        T Get<T>(string name) => (T)type.GetProperty(name, flags)!.GetValue(window)!;
        UIElement Named(string name) => (UIElement)window.FindName(name);
    }
}
