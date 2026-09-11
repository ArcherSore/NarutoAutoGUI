using System.Diagnostics;
using System.Text.Json;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Updates;

namespace NarutoAutoGUI;

public partial class App
{
    internal async Task InstallUpdateAsync(PreparedUpdate update)
    {
        if (_isExiting || _mainWindow is null || _sessionManager is null
            || _workerCoordinator is null || _logger is null) {
            return;
        }
        _isExiting = true;
        _mainWindow.SetExitInProgress(true);
        await _operationGate.WaitAsync();
        var handedOff = false;
        Process? trackedWorker = null;
        try {
            try {
                await Task.Run(() => UpdatePackage.Validate(update.Staging, update.Tag));
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or InvalidOperationException or KeyNotFoundException) {
                throw new InvalidDataException("暂存更新包已失效，请重新下载。", exception);
            }
            var updater = Path.Combine(AppContext.BaseDirectory, "NarutoAutoUpdater.exe");
            if (!File.Exists(updater)) {
                throw new FileNotFoundException("当前发布包缺少 Updater。", updater);
            }
            var temporary = Path.Combine(UpdateStorage.DirectoryFor(update.Installation), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var executable = Path.Combine(temporary, "NarutoAutoUpdater.exe");
            File.Copy(updater, executable);
            var probeStart = new ProcessStartInfo {
                FileName = executable, WorkingDirectory = temporary, UseShellExecute = false
            };
            probeStart.ArgumentList.Add("--probe");
            using (var probe = Process.Start(probeStart) ?? throw new IOException("无法启动 TEMP Updater。")) {
                await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (probe.ExitCode != 0) {
                    throw new IOException("TEMP Updater 无法运行，可能被系统保护策略阻止。");
                }
            }
            if (_workerCoordinator.TrackedWorkerPid is int workerPid) {
                try {
                    var process = Process.GetProcessById(workerPid);
                    _ = process.Handle;
                    trackedWorker = process;
                } catch (ArgumentException) {
                    // Already exited while the existing Session process list was being captured.
                }
            }
            _logger.Info("安装开始：停止 Run、Preview 和运行环境。");
            var activeRun = _workerCoordinator.Snapshot.WorkerSnapshot?.ActiveRun;
            if (activeRun is not null) {
                try {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _workerCoordinator.StopRunAsync(activeRun.RunId, timeout.Token);
                    while (_workerCoordinator.Snapshot.WorkerSnapshot?.ActiveRun is not null) {
                        await Task.Delay(200, timeout.Token);
                    }
                } catch (Exception exception) {
                    _logger.Warn("正常停止未确认，将复用现有 Child Session 注销清理。", exception);
                }
            }
            // The existing WTS logoff path ends only this application's Child Session and its programs.
            await _sessionManager.TerminateAsync();
            if (_sessionManager.HasChildSession) {
                throw new IOException("无法确认 Child Session 已结束。");
            }
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30))) {
                if (trackedWorker is not null) {
                    await trackedWorker.WaitForExitAsync(timeout.Token);
                }
            }
            _workerCoordinator.ChildSessionEnded();
            _logger.Info("Runtime shutdown 成功：已跟踪进程均已退出。");
            var handoffFile = Path.Combine(temporary, "handoff.json");
            File.WriteAllText(handoffFile, JsonSerializer.Serialize(new UpdateHandoff(update, Environment.ProcessId)));
            var start = new ProcessStartInfo {
                FileName = executable, WorkingDirectory = temporary, UseShellExecute = false
            };
            start.ArgumentList.Add(handoffFile);
            using var updaterProcess = Process.Start(start) ?? throw new IOException("无法启动 Updater。");
            handedOff = true;
            _logger.Info($"Updater 已启动，PID={updaterProcess.Id}；GUI 即将退出。");
            _trayIcon?.Dispose();
            _trayIcon = null;
            _sessionManager.Dispose();
            await _workerCoordinator.DisposeAsync();
            _workerCoordinator = null;
            _mainWindow.AllowClose();
            _mainWindow.Close();
            _logger.Dispose();
        } catch (Exception exception) when (handedOff) {
            _logger.Error("Updater 已接管，GUI 清理失败，继续退出。", exception);
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
