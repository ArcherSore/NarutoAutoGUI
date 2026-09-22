using System.Reflection;
using System.Runtime.InteropServices;
using NarutoAutoGUI.Infrastructure;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private async Task ExportDiagnosticsFromSettingsAsync()
    {
        var dialog = new SaveFileDialog {
            Title = "导出诊断包",
            FileName = $"NarutoAutoGUI-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "ZIP 压缩文件 (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true
        };
        if (dialog.ShowDialog(this) == true) {
            await ExportDiagnosticsAsync(dialog.FileName);
        }
    }

    private async Task ExportDiagnosticsAsync(string destination)
    {
        if (!_settings.ExportDiagnostics.IsEnabled) {
            return;
        }
        _settings.ExportDiagnostics.IsEnabled = false;
        _settings.ExportDiagnostics.Status = "正在导出…";
        try {
            var observation = _workerCoordinator.Snapshot;
            var worker = observation.WorkerSnapshot;
            var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
            var metadata = new DiagnosticMetadata(DateTime.UtcNow, version,
                _projectPlan?.ProjectName, _projectPlan?.ProjectVersion,
                Environment.OSVersion.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
                _sessionManager.Snapshot.State.ToString(),
                observation.Observation.ToString(), observation.SnapshotFresh,
                worker?.WorkerState.ToString(), worker?.WorkerVersion, worker?.ProtocolVersion);
            await Task.Run(() => new DiagnosticPackageExporter(_logger)
                .Export(destination, _applicationDirectory, _logger.LogDirectory, metadata));
            _settings.ExportDiagnostics.Status = "诊断包已导出";
        } catch (Exception exception) {
            _logger.Error("导出诊断包失败。", exception);
            _settings.ExportDiagnostics.Status = $"导出失败：{exception.GetBaseException().Message}";
        } finally {
            _settings.ExportDiagnostics.IsEnabled = true;
        }
    }
}
