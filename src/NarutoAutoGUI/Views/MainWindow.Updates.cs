using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using NarutoAutoGUI.Updates;
using Brush = System.Windows.Media.Brush;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private CancellationTokenSource? _updateCancellation;
    private EngineCheckResult? _updateCheck;
    private string? _preparedReference;
    private bool _preparingUpdate;
    private bool _updateBusy;
    private IInputElement? _updatePreviousFocus;

    private UpdateCheckState _updateCheckState;
    private string? _renderedUpdateNotes;

    // Presentation only; download, preparation and installation retain their existing operation gates.
    private enum UpdateCheckState
    {
        Idle, Checking, UpToDate, UpdateAvailable, CheckFailed
    }

    private static string UpdatePreferencePath => Path.Combine(AppContext.BaseDirectory, "config", "update-check.txt");

    private void InitializeUpdates()
    {
        UpdateCurrentVersionText.Text = $"当前版本：{_projectPlan?.ProjectVersion ?? "—"}";
        try {
            StartupUpdateCheck.IsChecked = !File.Exists(UpdatePreferencePath)
                || File.ReadAllText(UpdatePreferencePath) != "false";
            if (StartupUpdateCheck.IsChecked == true) {
                _ = CheckUpdateAsync();
            }
        } catch (Exception exception) {
            _logger.Warn("更新初始化不可用。", exception);
            _updateCheckState = UpdateCheckState.CheckFailed;
            UpdateCheckStatus.Text = "暂时无法检查更新，请确认使用完整发布包后重试。";
        }
        UpdateUpdaterControls();
    }

    private void StartupUpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdatePreferencePath)!);
            File.WriteAllText(UpdatePreferencePath, StartupUpdateCheck.IsChecked == true ? "true" : "false");
        } catch (Exception exception) {
            UpdateCheckStatus.Text = "无法保存更新设置，请重试。";
            _logger.Warn("保存更新设置失败。", exception);
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        OpenUpdateDialog();
        await CheckUpdateAsync();
    }

    private async Task CheckUpdateAsync()
    {
        if (_updateBusy || _exitInProgress) {
            return;
        }
        _updateBusy = true;
        _updateCancellation = new CancellationTokenSource();
        _updateCheck = null;
        _preparedReference = null;
        _updateCheckState = UpdateCheckState.Checking;
        UpdateDownloadStatus.Text = "";
        UpdateDownloadStatus.Visibility = Visibility.Collapsed;
        RefreshUpdateNotes();
        UpdateUpdaterControls();
        UpdateCheckStatus.Text = "正在检查更新…";
        try {
            var executable = Path.Combine(AppContext.BaseDirectory, "maanop-update-engine.exe");
            var engine = new UpdateEngineClient(new ProcessStartInfo(executable) {
                WorkingDirectory = AppContext.BaseDirectory
            });
            var result = await engine.CheckAsync(AppContext.BaseDirectory, _updateCancellation.Token);
            if (_exitInProgress) {
                _updateCheckState = UpdateCheckState.Idle;
                return;
            }
            _updateCheck = result;
            UpdateCurrentVersionText.Text = $"当前版本：{result.CurrentVersion}";
            _updateCheckState = result.Update is null
                ? UpdateCheckState.UpToDate : UpdateCheckState.UpdateAvailable;
            UpdateCheckStatus.Text = result.Update is null ? "当前已是最新版本" : "发现新版本，可查看更新。";
            RefreshUpdateNotes();
        } catch (OperationCanceledException) when (_exitInProgress) {
            // Exiting cancels this optional operation; it does not alter the runtime shutdown path.
            _updateCheckState = UpdateCheckState.Idle;
        } catch (Exception exception) {
            _logger.Warn("检查更新失败。", exception);
            _updateCheckState = UpdateCheckState.CheckFailed;
            if (!_exitInProgress) {
                UpdateCheckStatus.Text = exception is IOException or InvalidDataException
                    ? exception.Message : "检查更新失败，请确认 Update Engine 可运行后重试。";
            }
        } finally {
            _updateCancellation.Dispose();
            _updateCancellation = null;
            _updateBusy = false;
            UpdateUpdaterControls();
        }
    }

    private void RefreshUpdateNotes()
    {
        var notes = _updateCheck?.Update?.Notes ?? "";
        if (_renderedUpdateNotes == notes) {
            return;
        }
        _renderedUpdateNotes = notes;
        UpdateNotesViewer.Document = ReleaseNotesDocument.Create(
            string.IsNullOrWhiteSpace(notes) ? "此版本暂无更新说明。" : notes, OpenUpdateLink);
    }

    private void ViewUpdate_Click(object sender, RoutedEventArgs e)
    {
        // This footer action opens a dialog, not a navigation destination.
        e.Handled = true;
        OpenUpdateDialog();
    }

    private void OpenUpdateDialog()
    {
        if (UpdateOverlay.Visibility == Visibility.Visible || _exitInProgress) {
            return;
        }
        _updatePreviousFocus = Keyboard.FocusedElement;
        UpdateUpdaterControls();
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateDialogSize();
        EnsureModalWindowHook();
        UpdateOverlay.Focus();
    }

    private void CloseUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_exitInProgress) {
            return;
        }
        UpdateOverlay.Visibility = Visibility.Collapsed;
        if (PreviewOverlay.Visibility != Visibility.Visible) {
            RemovePreviewWindowHook();
        }
        if (_updatePreviousFocus is not null) {
            Keyboard.Focus(_updatePreviousFocus);
        }
    }

    private void UpdateOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDialogSize();

    private void UpdateDialogSize()
    {
        UpdateDialog.Width = Math.Min(360, Math.Max(1, UpdateOverlay.ActualWidth - 48));
        UpdateDialog.MaxHeight = Math.Max(1, Math.Min(600, UpdateOverlay.ActualHeight * 0.8));
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_updateCheck?.Update?.ReleaseUrl, UriKind.Absolute, out var uri)) {
            OpenUpdateLink(uri);
        } else {
            // Older V2 engines can still display their notes without a separate release URL.
            UpdateNotesPanel.BringIntoView();
            UpdateNotesViewer.Focus();
        }
    }

    private void OpenUpdateLink(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http")) {
            return;
        }
        try {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        } catch (Exception exception) {
            UpdateDownloadStatus.Text = "无法打开链接，请检查默认浏览器设置。";
            UpdateDownloadStatus.Visibility = Visibility.Visible;
            _logger.Warn("打开更新链接失败。", exception);
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _exitInProgress || _updateCheck?.Update is not EngineUpdate update) { return; }
        _updateBusy = _preparingUpdate = true;
        _preparedReference = null;
        _updateCancellation = new CancellationTokenSource();
        UpdateUpdaterControls();
        UpdateDownloadStatus.Text = "正在准备更新…";
        UpdateDownloadStatus.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Value = 0;
        try {
            var engine = new UpdateEngineClient(new ProcessStartInfo(
                Path.Combine(AppContext.BaseDirectory, "maanop-update-engine.exe")) {
                WorkingDirectory = AppContext.BaseDirectory
            });
            var progress = new Progress<EngineProgress>(value => {
                if (!_preparingUpdate || _exitInProgress) { return; }
                UpdateProgressBar.IsIndeterminate = value.Phase != "download" || value.Total <= 0;
                UpdateProgressBar.Value = value.Total > 0 ? 100.0 * value.Bytes / value.Total : 0;
                UpdateDownloadStatus.Text = value.Phase == "download"
                    ? $"已下载 {value.Bytes / 1048576.0:F1} / {value.Total / 1048576.0:F1} MB"
                        + $" · {value.BytesPerSecond / 1048576.0:F1} MB/s"
                    : "正在校验并解压完整包…";
            });
            _preparedReference = await engine.PrepareAsync(AppContext.BaseDirectory, update.Descriptor,
                progress, _updateCancellation.Token);
            UpdateDownloadStatus.Text = "更新已就绪。";
        } catch (OperationCanceledException) {
            UpdateDownloadStatus.Text = "已取消，可重新下载。";
        } catch (Exception exception) {
            _logger.Warn("准备更新失败。", exception);
            UpdateDownloadStatus.Text = exception.Message;
        } finally {
            _updateCancellation.Dispose();
            _updateCancellation = null;
            _updateBusy = _preparingUpdate = false;
            UpdateUpdaterControls();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _exitInProgress || _preparedReference is null) { return; }
        if (System.Windows.MessageBox.Show("安装更新将停止任务、关闭桌面分身及其中程序，并重启 MaaNOP。\n"
            + "保留 config、logs、debug、cache；\n"
            + "其余程序目录内容（包括自行添加的文件）会被替换或删除。是否继续？", "安装 MaaNOP 更新",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { return; }
        _updateBusy = true;
        UpdateUpdaterControls();
        try {
            await ((App)System.Windows.Application.Current).InstallUpdateAsync(_preparedReference);
        } catch (Exception exception) {
            _logger.Warn("安装交接失败。", exception);
            UpdateDownloadStatus.Text = exception.Message;
        } finally {
            _updateBusy = false;
            UpdateUpdaterControls();
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    private void UpdateUpdaterControls()
    {
        CheckUpdateButton.IsEnabled = !_updateBusy && !_exitInProgress;
        DialogCheckUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled;
        CloseUpdateButton.IsEnabled = !_exitInProgress;
        CancelDownloadButton.IsEnabled = !_exitInProgress;
        DownloadUpdateButton.IsEnabled = !_updateBusy && !_exitInProgress && _updateCheck?.Update is not null;
        DownloadUpdateButton.Visibility = _preparedReference is null ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = !_updateBusy && !_exitInProgress;
        InstallUpdateButton.Visibility = _preparedReference is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.Visibility = _preparingUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Visibility = _preparingUpdate ? Visibility.Visible : Visibility.Collapsed;
        RefreshUpdatePresentation();
    }

    private string CurrentUpdateVersion => _updateCheck?.CurrentVersion ?? _projectPlan?.ProjectVersion ?? "—";

    private void RefreshUpdatePresentation()
    {
        var checking = _updateCheckState == UpdateCheckState.Checking;
        var available = _updateCheckState == UpdateCheckState.UpdateAvailable;
        var latest = _updateCheckState == UpdateCheckState.UpToDate;
        var failed = _updateCheckState == UpdateCheckState.CheckFailed;
        UpdateNavigationBadge.Tag = _updateCheckState.ToString();
        UpdateNavigationBadge.Visibility = checking || available ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationItem.ToolTip = checking ? "正在检查更新…" : available ? "发现新版本" : "软件更新";
        System.Windows.Automation.AutomationProperties.SetName(
            UpdateNavigationItem, $"更新，{UpdateNavigationItem.ToolTip}");
        UpdateCheckingPanel.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        UpdateResultPanel.Visibility = checking ? Visibility.Collapsed : Visibility.Visible;
        DialogCheckUpdateButton.Visibility = checking || available ? Visibility.Collapsed : Visibility.Visible;
        DialogCheckUpdateButton.Content = failed ? "重试" : "检查更新";
        UpdateAvailableActions.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppIcon.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateStateIconBackground.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        UpdateStateIconBackground.Background = (Brush)FindResource(latest ? "Brush.Success" : "Brush.Primary.Surface");
        UpdateStateIcon.Foreground = (Brush)FindResource(latest ? "Brush.Text.Inverse" : "Brush.Primary");
        UpdateStateIcon.Symbol = latest ? Wpf.Ui.Controls.SymbolRegular.Checkmark24
            : failed ? Wpf.Ui.Controls.SymbolRegular.Warning24 : Wpf.Ui.Controls.SymbolRegular.ArrowSync24;
        UpdateStateTitle.Text = available ? "发现新版本" : latest ? "已是最新版本" : failed ? "检查更新失败" : "检查软件更新";
        UpdateVersionText.Text = available ? _updateCheck?.Update?.Version : CurrentUpdateVersion;
        UpdateInstalledVersionText.Text = available ? $"当前版本：{CurrentUpdateVersion}" : "";
        UpdateInstalledVersionText.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateResultMessage.Text = _updateCheckState switch {
            UpdateCheckState.CheckFailed => UpdateCheckStatus.Text,
            UpdateCheckState.UpToDate => "当前已是最新版本，感谢使用！",
            UpdateCheckState.UpdateAvailable => "",
            _ => "查看是否有可用的新版本。"
        };
        UpdateResultMessage.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        UpdateNotesPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesButton.Visibility = _preparingUpdate ? Visibility.Collapsed : Visibility.Visible;
        RefreshUpdateNotes();
    }
}
