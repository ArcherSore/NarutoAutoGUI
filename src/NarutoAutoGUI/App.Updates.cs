using System.Diagnostics;
using NarutoAutoGUI.Updates;

namespace NarutoAutoGUI;

public partial class App
{
    internal async Task InstallUpdateAsync(string reference)
    {
        if (IsExiting || _mainWindow is null || _sessionManager is null
            || _workerCoordinator is null || _logger is null || _environment is null) { return; }
        _environment.Closing = true;
        _mainWindow.SetExitInProgress(true);
        var handedOff = false;
        try {
            // The handoff runs before the gate is released, so no queued operation starts while it is in progress.
            await _environment.EndAsync(inGate: async ended => {
                if (ended.Error is { } error) {
                    throw new IOException($"{error.Message}已取消安装。请查看日志后重试。", error);
                }
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
            });
        } catch (Exception exception) when (handedOff) {
            _logger.Error("Engine 已接管，GUI 清理失败，继续退出。", exception);
        } finally {
            if (handedOff) {
                Shutdown(0);
            } else {
                _environment.Closing = false;
                _mainWindow.SetExitInProgress(false);
            }
        }
    }
}
