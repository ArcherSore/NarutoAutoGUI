using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;
using WpfButton = System.Windows.Controls.Button;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace NarutoAutoGUI.Infrastructure;

// Drives a real MainWindow through real seams and checks that state changes reach the runtime controls.
internal static partial class SelfTestRunner
{
    private static void VerifyRuntimeControlWiring(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "runtime-wiring");
        VerifyStartupAndExitWiring(logger, directory);
        VerifySessionRestoreAndEndWiring(logger, directory);
        VerifyFaultedSessionRetryWiring(logger, directory);
        VerifyWorkerStopWiring(logger, directory);
        VerifyStopWithoutProjectWiring(logger, directory);
        Console.WriteLine("RUNTIME CONTROL WIRING PASS: startup, exit, project failure, restore, end, retry, "
            + "stop, stop without project.");
    }

    private static void VerifyStartupAndExitWiring(AppLogger logger, string directory)
    {
        WithRuntimeControlWindow(logger, Path.Combine(directory, "startup"), window => {
            Require(HeaderShows(window, "PrepareEnvironmentButton", enabled: true) && ConfigurationEditable(window)
                && !Named<UIElement>(window, "ConfigurationLockBadge").IsVisible, "无分身启动后应可准备运行环境并编辑配置。");
            window.SetExitInProgress(true);
            Require(HeaderShows(window, "PrepareEnvironmentButton", enabled: false) && !ConfigurationEditable(window)
                && LockText(window) == "运行环境处理中，配置暂时锁定", "退出过程中应禁用运行命令并锁定配置。");
            window.SetExitInProgress(false);
            Require(HeaderShows(window, "PrepareEnvironmentButton", enabled: true) && ConfigurationEditable(window),
                "取消退出后应恢复运行命令与配置编辑。");
        });
        WithRuntimeControlWindow(logger, Path.Combine(directory, "no-project"), window => {
            Require(HeaderShows(window, "PrepareEnvironmentButton", enabled: false) && !ConfigurationEditable(window)
                && !Named<UIElement>(window, "ConfigurationLockBadge").IsVisible,
                "项目加载失败后不应能准备运行环境，也不显示配置锁定说明。");
        }, loadProject: false);
    }

    private static void VerifySessionRestoreAndEndWiring(AppLogger logger, string directory)
    {
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var session = wts.CreateManager(logger);
        var operations = new HeldOperations();
        WithRuntimeControlWindow(logger, Path.Combine(directory, "restore"), window => {
            PumpUntil(() => operations.Entered == 1, "启动时应开始恢复已有分身。");
            Require(ProgressShows(window, "正在准备运行环境") && LockText(window) == "运行环境处理中，配置暂时锁定"
                && !Named<UIElement>(window, "HomeDesktopVisibilityButton").IsEnabled, "恢复分身期间应显示准备进度并锁定配置。");
            operations.Release();
            PumpUntil(() => ConfigurationEditable(window), "恢复操作结束后应解除配置锁定。");
            Require(ProgressShows(window, "正在准备运行环境"), "分身仍处于 Existing 时应保持准备进度。");

            (bool Prepare, bool Editable)? duringLogoff = null;
            wts.DuringLogoff = _ => duringLogoff = (
                HeaderShows(window, "PrepareEnvironmentButton", enabled: false), ConfigurationEditable(window));
            var ending = session.TerminateAsync();
            PumpUntil(() => ending.IsCompleted, "结束分身未完成。");
            ending.GetAwaiter().GetResult();
            Require(duringLogoff == (true, false), "结束分身期间应禁用准备运行环境并锁定配置。");
            Require(HeaderShows(window, "PrepareEnvironmentButton", enabled: true) && ConfigurationEditable(window),
                "分身结束后应回到可准备运行环境。");
        }, session: session, runOperation: operations.RunAsync);
    }

    private static void VerifyFaultedSessionRetryWiring(AppLogger logger, string directory)
    {
        using var session = new ChildSessionManager(logger, () => throw new Win32Exception(5, "self-test WTS failure"),
            () => [0u, 1u], () => null);
        var operations = new HeldOperations();
        WithRuntimeControlWindow(logger, Path.Combine(directory, "faulted"), window => {
            Require(HeaderShows(window, "RetryEnvironmentButton", enabled: true), "分身故障时应显示可用的重试。");
            Click(window, "RetryEnvironmentButton");
            PumpUntil(() => operations.Entered == 1, "重试应开始准备运行环境。");
            Require(ProgressShows(window, "正在准备运行环境") && !ConfigurationEditable(window),
                "重试准备期间应显示准备进度并锁定配置。");
            operations.Release();
            PumpUntil(() => HeaderShows(window, "RetryEnvironmentButton", enabled: true), "准备结束后分身仍故障时应再次显示重试。");
        }, session: session, runOperation: operations.RunAsync);
    }

    private static void VerifyWorkerStopWiring(AppLogger logger, string directory)
    {
        var operations = new HeldOperations { RunOperations = true };
        var stoppingSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WithRunningFakeWorker(logger, Path.Combine(directory, "stop"), async (worker, cancellationToken) => {
            var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                worker.Pipe, ProtocolOperations.RunStop, cancellationToken);
            await worker.Pipe.WriteAsync(WireEnvelope.Response(ProtocolOperations.RunStop, stop.RequestId!.Value,
                new RunStopResponse("stop_requested")), cancellationToken);
            var stopping = worker.Coordinator.Snapshot.WorkerSnapshot! with {
                StateRevision = 2, RunState = RunState.Stopping,
                ActiveRun = worker.ActiveRun with { State = RunState.Stopping }
            };
            await worker.Pipe.WriteAsync(StateEvent(stopping), cancellationToken);
            stoppingSent.TrySetResult();
            await sendCancelled.Task.WaitAsync(cancellationToken);
            var cancelled = stopping with {
                StateRevision = 3, RunState = RunState.Idle, ActiveRun = null,
                LastRun = worker.ActiveRun with { State = RunState.Cancelled }
            };
            await worker.Pipe.WriteAsync(StateEvent(cancelled), cancellationToken);
        }, coordinator => WithRuntimeControlWindow(logger, Path.Combine(directory, "stop-window"), window => {
            PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true),
                "Worker 报告运行中的 Run 后应显示可用的停止任务。");
            Require(LockText(window) == "任务运行中，配置已锁定", "运行中应说明配置已锁定。");
            Click(window, "StopTaskHeaderButton");
            PumpUntil(() => operations.Entered == 1, "点击停止任务应开始停止操作。");
            Require(ProgressShows(window, "正在停止任务"), "停止操作进行中应显示停止进度。");
            operations.Release();
            PumpUntil(() => stoppingSent.Task.IsCompleted, "Worker 未收到停止请求。");
            PumpOnboarding();
            Require(ProgressShows(window, "正在停止任务"), "Run 停止中应保持停止进度。");
            sendCancelled.TrySetResult();
            PumpUntil(() => HeaderShows(window, "PrepareEnvironmentButton", enabled: true)
                && ConfigurationEditable(window), "Run 取消后应回到准备运行环境并解除配置锁定。");
        }, coordinator: coordinator, runOperation: operations.RunAsync));
    }

    private static void VerifyStopWithoutProjectWiring(AppLogger logger, string directory)
    {
        WithRunningFakeWorker(logger, Path.Combine(directory, "no-project-stop"), (_, _) => Task.CompletedTask,
            coordinator => WithRuntimeControlWindow(logger, Path.Combine(directory, "no-project-window"), window => {
                PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true),
                    "项目未加载时，运行中的 Run 仍应可以停止。");
            }, coordinator: coordinator, loadProject: false));
    }

    private sealed record FakeWorker(ProtocolConnection Pipe, WorkerCoordinator Coordinator, RunSnapshot ActiveRun);

    // A fake Worker reports a running Run, then follows the script; its pipe stays open until verify returns.
    private static void WithRunningFakeWorker(AppLogger logger, string directory,
        Func<FakeWorker, CancellationToken, Task> script, Action<WorkerCoordinator> verify)
    {
        var planDirectory = CreateProjectFixture(Path.Combine(directory, "plan"));
        var activeRun = WorkerCoordinatorSelfTest.CreateActiveRun(
            ProjectPlanModule.Open(planDirectory, Path.Combine(planDirectory, "plan.json")).CreateRunStartAttempt());
        WithFakeWorker(logger, Path.Combine(directory, "worker"), (coordinator, record, pipeName) => {
            var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var worker = Task.Run(async () => {
                await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, timeout.Token, activeRun);
                await script(new FakeWorker(pipe, coordinator, activeRun), timeout.Token);
                await close.Task.WaitAsync(timeout.Token);
            });
            var verified = false;
            try {
                verify(coordinator);
                verified = true;
            } finally {
                close.TrySetResult();
                // A failed check stops the fake Worker at once, so its timeout cannot hide the check's message.
                if (!verified) {
                    timeout.Cancel();
                }
                try {
                    worker.GetAwaiter().GetResult();
                } catch (Exception) when (!verified) {
                }
            }
        });
    }

    private static WireEnvelope StateEvent(WorkerSnapshot snapshot) => WireEnvelope.Event(
        ProtocolOperations.RunStateChanged,
        new StateChangedEvent(snapshot.WorkerInstanceId, snapshot.StateRevision, snapshot));

    // A coordinator whose admission belongs to this process, so a pipe client from the test is admitted.
    private static void WithFakeWorker(AppLogger logger, string directory,
        Action<WorkerCoordinator, WorkerAdmissionRecord, string> verify)
    {
        Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        var record = new WorkerAdmissionRecord(Guid.NewGuid(),
            Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(), checked((uint)process.SessionId),
            Environment.ProcessId, "self-test-runtime", DateTime.UtcNow);
        File.WriteAllBytes(Path.Combine(directory, "worker.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options));
        var pipeName = $"NarutoAutoGUI.RuntimeWiring.SelfTest.{Guid.NewGuid():N}";
        var coordinator = new WorkerCoordinator(logger, directory,
            process.MainModule?.FileName ?? throw new InvalidOperationException("无法取得自检进程路径。"),
            pipeName, usePipeAcl: false);
        try {
            Task.Run(() => coordinator.WaitForServerReadyAsync(CancellationToken.None)).GetAwaiter().GetResult();
            verify(coordinator, record, pipeName);
        } finally {
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    private static bool HeaderShows(MainWindow window, string control, bool enabled) =>
        ShownHeaderControls(window).SequenceEqual([control]) && Named<UIElement>(window, control).IsEnabled == enabled;

    private static bool ProgressShows(MainWindow window, string text)
    {
        var ring = Named<FrameworkElement>(window, "RuntimeHeaderProgressRing");
        return HeaderShows(window, "RuntimeHeaderProgressRing", ring.IsEnabled) && Equals(ring.ToolTip, text);
    }

    private static bool ConfigurationEditable(MainWindow window) =>
        Named<UIElement>(window, "NewConfigurationButton").IsEnabled;

    private static string? LockText(MainWindow window) =>
        Named<UIElement>(window, "ConfigurationLockBadge").IsVisible
            ? Named<TextBlock>(window, "ConfigurationLockText").Text : null;

    private static void Click(MainWindow window, string name)
    {
        Named<WpfButton>(window, name).RaiseEvent(new RoutedEventArgs(WpfButtonBase.ClickEvent));
        PumpOnboarding(20);
    }

    private static void PumpUntil(Func<bool> condition, string failure)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (!condition()) {
            if (Environment.TickCount64 > deadline) {
                throw new InvalidOperationException(failure);
            }
            PumpOnboarding(20);
        }
    }

    // Stands in for the application operation gate: each operation waits until the test releases it.
    private sealed class HeldOperations
    {
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Entered { get; private set; }

        internal bool RunOperations { get; init; }

        internal async Task RunAsync(Func<Task> operation)
        {
            Entered++;
            await _release.Task;
            if (RunOperations) {
                await operation();
            }
        }

        internal void Release()
        {
            var release = _release;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release.TrySetResult();
        }
    }
}
