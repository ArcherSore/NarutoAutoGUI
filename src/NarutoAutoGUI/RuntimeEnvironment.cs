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
    // for each wait and serialises operations through its own gate.
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

    // Each reason keeps the steps its caller has always taken; confirm is asked only where the reason asks for it.
    // Exit and install run inGate with the result before releasing the gate, so the caller's dialog, teardown or
    // install handoff happens before any queued operation.
    internal Task<EnvironmentEndResult> EndAsync(EnvironmentEndReason reason, Func<bool>? confirm = null,
        Func<EnvironmentEndResult, Task>? inGate = null) => reason switch {
            EnvironmentEndReason.Exit => EndExclusivelyAsync(() => EndForExitAsync(confirm), inGate),
            EnvironmentEndReason.Install => EndExclusivelyAsync(EndForInstallAsync, inGate),
            EnvironmentEndReason.UserRequested => EndForUserAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };

    private async Task<EnvironmentEndResult> EndExclusivelyAsync(
        Func<Task<EnvironmentEndResult>> end, Func<EnvironmentEndResult, Task>? inGate)
    {
        var result = EnvironmentEndResult.Ended;
        await RunGatedAsync(async () => {
            result = await end();
            if (inGate is not null) {
                await inGate(result);
            }
        }, refuseWhileClosing: false);
        return result;
    }

    private async Task<EnvironmentEndResult> EndForExitAsync(Func<bool>? confirm)
    {
        bool hasChildSession;
        try {
            // The gate is held, so this observes the state after any in-flight create, connect or launch operation.
            hasChildSession = _session.HasChildSession;
        } catch (Exception exception) {
            _logger.Error("退出前检查 Child Session 失败。", exception);
            return EnvironmentEndResult.Unconfirmed("无法确认桌面分身状态，已取消退出。请查看日志后重试。", exception);
        }

        if (hasChildSession) {
            if (confirm?.Invoke() == false) {
                return EnvironmentEndResult.Declined;
            }
            try {
                await _session.TerminateAsync();
                _coordinator.ChildSessionEnded();
            } catch (Exception exception) {
                _logger.Critical("退出前注销 Child Session 失败，已取消退出。", exception);
                return EnvironmentEndResult.Unconfirmed("注销桌面分身失败，已取消退出，避免遗留未知状态。请查看日志。", exception);
            }
        }

        try {
            if (_session.HasChildSession) {
                _logger.Critical("最终退出检查仍检测到 Child Session，已取消退出。");
                return EnvironmentEndResult.Unconfirmed("最终检查仍检测到桌面分身，已取消退出，避免遗留未知状态。请查看日志后重试。");
            }
        } catch (Exception exception) {
            _logger.Critical("最终退出检查 Child Session 失败，已取消退出。", exception);
            return EnvironmentEndResult.Unconfirmed("最终检查无法确认桌面分身状态，已取消退出。请查看日志后重试。", exception);
        }
        return EnvironmentEndResult.Ended;
    }

    // The user has already confirmed; the Admission is discarded once the gate is released.
    private async Task<EnvironmentEndResult> EndForUserAsync()
    {
        try {
            await RunGatedAsync(() => _session.TerminateAsync(), refuseWhileClosing: true);
            _coordinator.ChildSessionEnded();
            return EnvironmentEndResult.Ended;
        } catch (OperationCanceledException) when (Closing) {
            throw;
        } catch (Exception exception) {
            return EnvironmentEndResult.Unconfirmed(exception);
        }
    }

    private async Task<EnvironmentEndResult> EndForInstallAsync()
    {
        using var exitWait = new CancellationTokenSource();
        try {
            // Capture the already verified Worker before WTS teardown, so a reused PID cannot pass for its exit.
            var workerExit = _coordinator.TrackedWorkerPid is int pid ? _waitForWorkerExit(pid, exitWait.Token) : null;
            _logger.Info("安装前停止任务与 Preview，随后注销既有 Child Session。");
            await StopActiveRunAsync();
            await _session.TerminateAsync();
            if (_session.HasChildSession) {
                throw new IOException("无法确认桌面分身已结束，已取消安装。请查看日志后重试。");
            }
            if (workerExit is not null) {
                try {
                    await workerExit.WaitAsync(_waitLimit);
                } catch (TimeoutException) {
                    throw new IOException("无法确认 Worker 已退出，已取消安装。请查看日志后重试。");
                }
            }
            _coordinator.ChildSessionEnded();
            _logger.Info("已确认 Child Session 与已跟踪 Worker 结束，开始安装交接。");
            return EnvironmentEndResult.Ended;
        } catch (Exception exception) {
            return EnvironmentEndResult.Unconfirmed(exception);
        } finally {
            exitWait.Cancel();
        }
    }

    private async Task StopActiveRunAsync()
    {
        if (_coordinator.Snapshot.WorkerSnapshot?.ActiveRun is not { } activeRun) {
            return;
        }
        try {
            using var stopTimeout = new CancellationTokenSource(_waitLimit);
            await _coordinator.StopRunAsync(activeRun.RunId, stopTimeout.Token);
            while (_coordinator.Snapshot.WorkerSnapshot?.ActiveRun is not null) {
                await Task.Delay(200, stopTimeout.Token);
            }
        } catch (Exception exception) {
            _logger.Warn("正常停止未确认，继续使用现有 Child Session 注销路径。", exception);
        }
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

internal enum EnvironmentEndReason
{
    Exit, UserRequested, Install
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
