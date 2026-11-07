using System.Diagnostics;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Settings;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private DebugFileCleaner DebugFiles => new(_applicationDirectory, _logger.LogDirectory, _logger);

    private Task ExportDiagnosticsFromSettingsAsync() => ExportDiagnosticsAsync(RevealInExplorer);

    private async Task ExportDiagnosticsAsync(Action<string> reveal)
    {
        var action = _settings.ExportDiagnostics;
        SetDebugFileActionsEnabled(false);
        action.Status = "正在导出…";
        try {
            var observation = _workerCoordinator.Snapshot;
            var worker = observation.WorkerSnapshot;
            // The log already holds every change of state; this line says where things stood when the user exported.
            _logger.Info($"开始导出诊断包{AppLogger.Field("version", App.ApplicationVersion)}"
                + $"{AppLogger.Field("project", _projectPlan?.ProjectName)}"
                + $"{AppLogger.Field("projectVersion", _projectPlan?.ProjectVersion)}"
                + $"{AppLogger.Field("childSession", _sessionManager.Snapshot.State)}"
                + $"{AppLogger.Field("observation", observation.Observation)}"
                + $"{AppLogger.Field("fresh", observation.SnapshotFresh)}"
                + $"{AppLogger.Field("workerState", worker?.WorkerState)}"
                + $"{AppLogger.Field("activeRun", worker?.ActiveRun?.RunId)}");
            var (project, version) = (_projectPlan?.ProjectName, _projectPlan?.ProjectVersion);
            var result = await Task.Run(() => new DiagnosticPackageExporter(_logger).Export(
                _applicationDirectory, _logger.LogDirectory, project, version, DateTime.Now));
            action.Status = result.Volumes == 1 ? "诊断包已导出。" : $"诊断包已导出，共 {result.Volumes} 卷。";
            reveal(result.FirstVolume);
        } catch (Exception exception) {
            _logger.Error("导出诊断包失败。", exception);
            action.Status = $"导出失败：{exception.GetBaseException().Message}";
        } finally {
            SetDebugFileActionsEnabled(true);
        }
        RefreshDebugFileUsage();
    }

    // A clean during a multi-volume export would delete the finished volumes of a package reported as complete.
    private void SetDebugFileActionsEnabled(bool enabled)
    {
        _settings.ExportDiagnostics.IsEnabled = enabled;
        _settings.ClearDebugFiles.IsEnabled = enabled;
    }

    private void RevealInExplorer(string file)
    {
        try {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        } catch (Exception exception) {
            // The package exists; only showing it failed.
            _logger.Warn($"无法在资源管理器中定位诊断包：{file}。", exception);
            _settings.ExportDiagnostics.Status = $"诊断包已导出：{file}";
        }
    }

    private async Task ClearDebugFilesAsync()
    {
        var action = _settings.ClearDebugFiles;
        SetDebugFileActionsEnabled(false);
        action.Status = "正在清理…";
        try {
            var result = await Task.Run(DebugFiles.Clean);
            action.Status = $"已释放 {DebugFileCleaner.FormatBytes(result.ReclaimedBytes)}。";
        } catch (Exception exception) {
            _logger.Error("清理日志和调试文件失败。", exception);
            action.Status = $"清理失败：{exception.GetBaseException().Message}";
        } finally {
            SetDebugFileActionsEnabled(true);
        }
    }

    private async void RefreshDebugFileUsage()
    {
        var action = _settings.ClearDebugFiles;
        if (!action.IsEnabled) {
            return;
        }
        try {
            var bytes = await Task.Run(DebugFiles.MeasureBytes);
            if (action.IsEnabled) {
                action.Status = $"当前可清理 {DebugFileCleaner.FormatBytes(bytes)}。";
            }
        } catch (Exception exception) {
            _logger.Warn("统计日志和调试文件占用失败。", exception);
        }
    }

    // A Worker that survived the last GUI may still be writing evidence of the Run in progress.
    private void ClearDebugFilesOnStartup()
    {
        if (_settings.ClearOnStartup.Value
            && _workerCoordinator.Snapshot.Observation == Worker.WorkerObservation.WorkerNotStarted) {
            _ = ClearDebugFilesAsync();
        }
    }

    private async void OnFrameworkOptionsChanged(SettingsToggle toggle)
    {
        // An exception leaving an async void handler would end the process.
        try {
            await _workerCoordinator.ApplyFrameworkOptionsAsync();
        } catch (OperationCanceledException) {
            // The connection ended mid-request; the next one applies the same choice.
        } catch (Exception) {
            // The coordinator has logged the failure; the next connection applies the same choice.
            toggle.Status = "未能应用到运行中的 Worker，重新连接后会再次尝试。";
        }
    }
}
