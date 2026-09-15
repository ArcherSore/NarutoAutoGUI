using System.Diagnostics;
using NarutoAutoGUI.Updates;

namespace NarutoAutoGUI;

public partial class App
{
    internal async Task InstallUpdateAsync(string reference)
    {
        if (_isExiting || _mainWindow is null || _sessionManager is null
            || _workerCoordinator is null || _logger is null) { return; }
        _isExiting = true;
        _mainWindow.SetExitInProgress(true);
        await _operationGate.WaitAsync();
        var handedOff = false;
        Process? trackedWorker = null;
        try {
            // Capture the already verified Worker handle before WTS teardown clears its admission record.
            if (_workerCoordinator.TrackedWorkerPid is int workerPid) {
                try {
                    trackedWorker = Process.GetProcessById(workerPid);
                    _ = trackedWorker.Handle;
                } catch (ArgumentException) {
                    // The previously tracked Worker has already exited.
                }
            }
            _logger.Info("安装前停止任务与 Preview，随后注销既有 Child Session。");
            var activeRun = _workerCoordinator.Snapshot.WorkerSnapshot?.ActiveRun;
            if (activeRun is not null) {
                try {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _workerCoordinator.StopRunAsync(activeRun.RunId, stopTimeout.Token);
                    while (_workerCoordinator.Snapshot.WorkerSnapshot?.ActiveRun is not null) {
                        await Task.Delay(200, stopTimeout.Token);
                    }
                } catch (Exception exception) {
                    _logger.Warn("正常停止未确认，继续使用现有 Child Session 注销路径。", exception);
                }
            }
            await _sessionManager.TerminateAsync();
            if (_sessionManager.HasChildSession) {
                throw new IOException("无法确认桌面分身已结束，已取消安装。请查看日志后重试。");
            }
            if (trackedWorker is not null) {
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try {
                    await trackedWorker.WaitForExitAsync(exitTimeout.Token);
                } catch (OperationCanceledException) {
                    throw new IOException("无法确认 Worker 已退出，已取消安装。请查看日志后重试。");
                }
            }
            _workerCoordinator.ChildSessionEnded();
            _logger.Info("已确认 Child Session 与已跟踪 Worker 结束，开始安装交接。");
            var engine = new UpdateEngineClient(new ProcessStartInfo(
                Path.Combine(AppContext.BaseDirectory, "maanop-update-engine.exe")) {
                WorkingDirectory = AppContext.BaseDirectory
            });
            await engine.InstallAsync(AppContext.BaseDirectory, reference, Environment.ProcessId, default);
            handedOff = true;
            _logger.Info("实际 Update Engine 已 ready；GUI 即将退出。");
            _trayIcon?.Dispose();
            _trayIcon = null;
            _sessionManager.Dispose();
            await _workerCoordinator.DisposeAsync();
            _workerCoordinator = null;
            _mainWindow.AllowClose();
            _mainWindow.Close();
            _logger.Dispose();
        } catch (Exception exception) when (handedOff) {
            _logger.Error("Engine 已接管，GUI 清理失败，继续退出。", exception);
        } finally {
            trackedWorker?.Dispose();
            _operationGate.Release();
            if (handedOff) {
                Shutdown(0);
            } else {
                _isExiting = false;
                _mainWindow.SetExitInProgress(false);
            }
        }
    }
}
