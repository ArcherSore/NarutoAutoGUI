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
        try {
            if (_sessionManager.HasChildSession || _workerCoordinator.TrackedWorkerPid is not null) {
                throw new IOException("请先结束运行环境，再安装更新。");
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
        } catch (Exception exception) when (handedOff) {
            _logger.Error("Engine 已接管，GUI 清理失败，继续退出。", exception);
        } finally {
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
