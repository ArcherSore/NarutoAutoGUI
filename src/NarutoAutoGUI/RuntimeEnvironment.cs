using System.Diagnostics;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI;

// The single owner of the Runtime Environment lifecycle: the application operation gate, preparing the environment
// and ending it. Callers keep their dialogs; WTS, RDP and Admission rules stay in ChildSessionManager and
// WorkerCoordinator.
internal sealed class RuntimeEnvironment
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppLogger _logger;
    private readonly ChildSessionManager _session;
    private readonly WorkerCoordinator _coordinator;
    private readonly ChildSessionProgramService _programService;
    private readonly Func<int, CancellationToken, Task> _waitForWorkerExit;
    private readonly TimeSpan _waitLimit;
    private readonly Func<Func<Task>, Task>? _runExclusive;

    // The optional parameters are self-test seams. Production waits for the Worker process itself, allows 30 seconds
    // each for the Run to stop and for the Worker to exit, and serialises operations through its own gate.
    internal RuntimeEnvironment(
        AppLogger logger, ChildSessionManager session, WorkerCoordinator coordinator,
        ChildSessionProgramService programService, Func<int, CancellationToken, Task>? waitForWorkerExit = null,
        TimeSpan? waitLimit = null, Func<Func<Task>, Task>? runExclusive = null)
    {
        _logger = logger;
        _session = session;
        _coordinator = coordinator;
        _programService = programService;
        _waitForWorkerExit = waitForWorkerExit ?? WaitForProcessExit;
        _waitLimit = waitLimit ?? TimeSpan.FromSeconds(30);
        _runExclusive = runExclusive;
    }

    // Set while the application exits or hands off an install; operations that have not started are refused.
    internal bool Closing { get; set; }

    // True from entering the gate to end the Runtime Environment until that end returns.
    internal bool Ending { get; private set; }

    // True after an end that could not confirm the Child Session and the Worker were gone, until one does.
    internal bool EndUnconfirmed { get; private set; }

    // Raised when Ending or EndUnconfirmed changes, on the thread running the end.
    internal event EventHandler? StateChanged;

    internal Task RunExclusiveAsync(Func<Task> operation) => RunGatedAsync(operation, refuseWhileClosing: true);

    // Runs inside an exclusive operation the caller has already entered. The framework options are read once the
    // Child Session is connected, just before the Worker is prepared.
    internal async Task PrepareAsync(ProjectPlanModule project, Func<MaaFrameworkOptions> frameworkOptions)
    {
        var sessionId = await _session.EnsureConnectedAsync(showPreview: true);
        await _coordinator.PrepareWorkerAsync(sessionId, project, frameworkOptions: frameworkOptions());
        var game = NarutoGameLaunchProfile.ResolveExisting(_logger);
        await _programService.LaunchIfNeededAsync(
            sessionId, game.ExecutablePath, game.Arguments,
            launchedProcessName: NarutoGameLaunchProfile.ClientProcessName);
        _session.ShowPreview();
        _logger.Info("真实 E2E 环境已准备；完成游戏登录后即可开始任务。 ");
    }

    // Every caller ends the same way. confirm is asked inside the gate, and only when a Runtime Environment may
    // remain; inGate receives the result before the gate is released, so a caller's teardown or install handoff
    // happens before any queued operation. The closing flag never refuses an end.
    internal async Task<EnvironmentEndResult> EndAsync(
        Func<bool>? confirm = null, Func<EnvironmentEndResult, Task>? inGate = null)
    {
        var result = EnvironmentEndResult.Ended;
        await RunGatedAsync(async () => {
            SetState(ending: true, EndUnconfirmed);
            try {
                result = await EndInGateAsync(confirm);
            } finally {
                SetState(ending: false, EndUnconfirmed);
            }
            if (inGate is not null) {
                await inGate(result);
            }
        }, refuseWhileClosing: false);
        return result;
    }

    private async Task<EnvironmentEndResult> EndInGateAsync(Func<bool>? confirm)
    {
        bool hasChildSession;
        try {
            // The gate is held, so this observes the state after any in-flight create, connect or launch operation.
            hasChildSession = _session.HasChildSession;
        } catch (Exception exception) {
            return Unconfirmed("无法确认桌面分身状态。", exception);
        }
        // Any other observation may still hold an Admission, and so a Worker that has not been proven gone.
        if (!hasChildSession && _coordinator.Snapshot.Observation
            is WorkerObservation.WorkerNotStarted or WorkerObservation.ChildSessionEnded) {
            SetState(Ending, endUnconfirmed: false);
            return EnvironmentEndResult.Ended;
        }
        if (confirm?.Invoke() == false) {
            return EnvironmentEndResult.Declined;
        }

        using var exitWait = new CancellationTokenSource();
        var failure = "无法确认 Worker 已退出。";
        try {
            // Capture the already verified Worker before WTS teardown, so a reused PID cannot pass for its exit.
            var workerExit = _coordinator.TrackedWorkerPid is int pid ? _waitForWorkerExit(pid, exitWait.Token) : null;
            _logger.Info("开始结束桌面分身：先停止任务，随后注销 Child Session。");
            await StopActiveRunAsync();
            failure = "注销桌面分身失败。";
            await _session.TerminateAsync();
            failure = "无法确认桌面分身已结束。";
            if (_session.HasChildSession) {
                return Unconfirmed(failure);
            }
            // Without a tracked PID, the logged-off Child Session is the proof that no Worker remains.
            failure = "无法确认 Worker 已退出。";
            if (workerExit is not null) {
                await workerExit.WaitAsync(_waitLimit);
            }
            failure = "丢弃 Worker Admission 失败。";
            _coordinator.ChildSessionEnded();
        } catch (Exception exception) {
            return Unconfirmed(failure, exception);
        } finally {
            exitWait.Cancel();
        }
        SetState(Ending, endUnconfirmed: false);
        _logger.Info("已确认 Child Session 结束、Worker 退出，Admission 已丢弃。");
        return EnvironmentEndResult.Ended;
    }

    // Stops only a Run that a connected, healthy Worker can still stop, then waits for a fresh snapshot without it.
    // A failure here only logs: logging off the Child Session ends whatever still runs in it.
    private async Task StopActiveRunAsync()
    {
        if (_coordinator.Snapshot is not { Observation: WorkerObservation.Connected, SnapshotFresh: true,
            WorkerSnapshot: { WorkerState: not WorkerState.Faulted, ActiveRun: { } run } }) {
            return;
        }
        try {
            using var stopTimeout = new CancellationTokenSource(_waitLimit);
            if (run.State != RunState.Stopping) {
                await _coordinator.StopRunAsync(run.RunId, stopTimeout.Token);
            }
            while (_coordinator.Snapshot is not { Observation: WorkerObservation.Connected, SnapshotFresh: true }
                || _coordinator.Snapshot.WorkerSnapshot?.ActiveRun?.RunId == run.RunId) {
                if (_coordinator.Snapshot is { Observation: not WorkerObservation.Connected }
                    or { WorkerSnapshot.WorkerState: WorkerState.Faulted }) {
                    throw new IOException("Worker 已断开或进入 Faulted。");
                }
                await Task.Delay(200, stopTimeout.Token);
            }
            _logger.Info($"Active Run 已正常停止：runId={run.RunId:D}。");
        } catch (Exception exception) {
            _logger.Warn("正常停止未确认，继续注销 Child Session。", exception);
        }
    }

    private EnvironmentEndResult Unconfirmed(string message, Exception? cause = null)
    {
        _logger.Critical($"结束桌面分身未确认：{message}已保留 Worker Admission。", cause);
        SetState(Ending, endUnconfirmed: true);
        return EnvironmentEndResult.Unconfirmed(message, cause);
    }

    private void SetState(bool ending, bool endUnconfirmed)
    {
        if (Ending == ending && EndUnconfirmed == endUnconfirmed) {
            return;
        }
        Ending = ending;
        EndUnconfirmed = endUnconfirmed;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunGatedAsync(Func<Task> operation, bool refuseWhileClosing)
    {
        ThrowIfRefused(refuseWhileClosing);
        if (_runExclusive is not null) {
            await _runExclusive(async () => {
                ThrowIfRefused(refuseWhileClosing);
                await operation();
            });
            return;
        }
        await _gate.WaitAsync();
        try {
            ThrowIfRefused(refuseWhileClosing);
            await operation();
        } finally {
            _gate.Release();
        }
    }

    private void ThrowIfRefused(bool refuseWhileClosing)
    {
        if (refuseWhileClosing && Closing) {
            throw new OperationCanceledException("应用正在退出，未开始新的操作。");
        }
    }

    // The handle is opened before this returns, so the wait follows the Worker even after its PID is released.
    private static Task WaitForProcessExit(int pid, CancellationToken cancellationToken)
    {
        Process process;
        try {
            process = Process.GetProcessById(pid);
        } catch (ArgumentException) {
            return Task.CompletedTask;
        }
        try {
            _ = process.Handle;
        } catch {
            process.Dispose();
            throw;
        }
        return WaitAndDisposeAsync(process, cancellationToken);

        static async Task WaitAndDisposeAsync(Process process, CancellationToken cancellationToken)
        {
            using (process) {
                await process.WaitForExitAsync(cancellationToken);
            }
        }
    }
}

internal enum EnvironmentEndOutcome
{
    Ended, Declined, Unconfirmed
}

// Error explains an unconfirmed end; it is null when the environment ended or the user declined.
internal sealed record EnvironmentEndResult(EnvironmentEndOutcome Outcome, Exception? Error = null)
{
    internal static EnvironmentEndResult Ended { get; } = new(EnvironmentEndOutcome.Ended);

    internal static EnvironmentEndResult Declined { get; } = new(EnvironmentEndOutcome.Declined);

    internal static EnvironmentEndResult Unconfirmed(Exception error) => new(EnvironmentEndOutcome.Unconfirmed, error);

    internal static EnvironmentEndResult Unconfirmed(string message, Exception? cause = null) =>
        Unconfirmed(new InvalidOperationException(message, cause));
}
