using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using NarutoAutoGUI.Updates;
using Brush = System.Windows.Media.Brush;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private readonly HttpClient _updateClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _updateCancellation;
    private UpdateSource? _updateSource;
    private UpdateRelease? _availableUpdate;
    private UpdateCompletion? _updateCompletion;
    private PreparedUpdate? _preparedUpdate;
    private bool _updateBusy;
    private IInputElement? _updatePreviousFocus;
    private UpdateCheckState _updateCheckState;
    private string? _renderedUpdateNotes;

    // Presentation only; download, preparation and installation retain their existing operation gates.
    private enum UpdateCheckState
    {
        Idle, Checking, UpToDate, UpdateAvailable, CheckFailed
    }

    private void InitializeUpdates()
    {
        UpdateCurrentVersionText.Text = $"当前版本：{_projectPlan?.ProjectVersion ?? "—"}";
        try {
            var cache = UpdateStorage.DirectoryFor(AppContext.BaseDirectory);
            var preference = Path.Combine(cache, "check-at-startup.txt");
            StartupUpdateCheck.IsChecked = !File.Exists(preference) || File.ReadAllText(preference) != "false";
            _updateSource = UpdateSource.Load(AppContext.BaseDirectory);
            UpdateCurrentVersionText.Text = $"当前版本：{_updateSource.Version}";
            _updateCompletion = UpdateStorage.TakeCompletion(AppContext.BaseDirectory, _updateSource.Version);
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
            File.WriteAllText(
                Path.Combine(UpdateStorage.DirectoryFor(AppContext.BaseDirectory), "check-at-startup.txt"),
                StartupUpdateCheck.IsChecked == true ? "true" : "false");
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
        if (_updateBusy || _exitInProgress || _preparedUpdate is not null) {
            return;
        }
        _updateCheckState = UpdateCheckState.Checking;
        UpdateDownloadStatus.Text = "";
        UpdateDownloadStatus.Visibility = Visibility.Collapsed;
        SetUpdateBusy(true);
        var cancellation = _updateCancellation!.Token;
        UpdateCheckStatus.Text = "正在检查更新…";
        try {
            _updateSource = UpdateSource.Load(AppContext.BaseDirectory);
            UpdateCurrentVersionText.Text = $"当前版本：{_updateSource.Version}";
            var service = new UpdateService(_updateClient, message => _logger.Info(message));
            _availableUpdate = await service.CheckAsync(_updateSource, cancellation);
            _updateCheckState = _availableUpdate is null
                ? UpdateCheckState.UpToDate : UpdateCheckState.UpdateAvailable;
            UpdateCheckStatus.Text = _availableUpdate is null ? "当前已是最新版本" : "发现新版本，可查看更新。";
        } catch (Exception exception) {
            _logger.Warn("检查更新失败。", exception);
            _availableUpdate = null;
            _updateCheckState = UpdateCheckState.CheckFailed;
            UpdateCheckStatus.Text = exception is InvalidDataException
                ? exception.Message : "检查更新失败，请检查网络连接后重试。";
        } finally {
            SetUpdateBusy(false);
        }
    }

    private void RefreshUpdateNotes()
    {
        var notes = _availableUpdate?.Notes ?? _updateCompletion?.Notes ?? "";
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
        var tag = _availableUpdate?.Tag ?? _updateCompletion?.Tag;
        if (_updateSource is not null && tag is not null) {
            OpenUpdateLink(new Uri($"https://github.com/{_updateSource.Repository}/releases/tag/"
                + Uri.EscapeDataString(tag)));
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
        if (_updateBusy || _availableUpdate is null || _exitInProgress) {
            return;
        }
        SetUpdateBusy(true);
        var cancellation = _updateCancellation!.Token;
        CancelDownloadButton.Visibility = Visibility.Visible;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateDownloadStatus.Text = "正在下载…";
        UpdateDownloadStatus.Visibility = Visibility.Visible;
        string? zip = null;
        string? staging = null;
        var ready = false;
        try {
            var release = _availableUpdate;
            var installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            zip = Path.Combine(UpdateStorage.DirectoryFor(installation), $"{Guid.NewGuid():N}.zip");
            staging = installation + $".update-{Guid.NewGuid():N}";
            var progress = new Progress<DownloadProgress>(value =>
            {
                UpdateProgressBar.Value = 100.0 * value.Received / value.Total;
                UpdateDownloadStatus.Text = value.Verifying ? "正在校验更新包…"
                    : $"{UpdateProgressBar.Value:0}%\n{value.Received / 1048576.0:0.0} MB / "
                        + $"{value.Total / 1048576.0:0.0} MB   {value.BytesPerSecond / 1048576:0.0} MB/s";
            });
            var service = new UpdateService(_updateClient, message => _logger.Info(message));
            await service.DownloadAsync(release, zip, progress, cancellation);
            UpdateDownloadStatus.Text = "正在验证更新包…";
            UpdateProgressBar.IsIndeterminate = true;
            await Task.Run(() => UpdatePackage.Extract(zip, staging, release.Tag, cancellation));
            _logger.Info("更新包预验证成功。");
            _preparedUpdate = new PreparedUpdate(installation, staging, release.Tag, release.Notes);
            ready = true;
            UpdateDownloadStatus.Text = "更新已就绪，可安装并重启。";
        } catch (OperationCanceledException) {
            UpdateDownloadStatus.Text = "下载已取消或超时，请重新下载。";
        } catch (Exception exception) {
            _logger.Warn("下载或更新包校验失败。", exception);
            UpdateDownloadStatus.Text = exception is InvalidDataException ? exception.Message : "下载失败，请重新下载。";
        } finally {
            try {
                if (zip is not null && File.Exists(zip)) {
                    File.Delete(zip);
                }
                if (!ready && staging is not null && Directory.Exists(staging)) {
                    UpdatePackage.RejectLinks(staging);
                    Directory.Delete(staging, true);
                }
            } catch (Exception exception) {
                _logger.Warn("清理更新缓存失败。", exception);
            }
            SetUpdateBusy(false, resetDownloadUi: true);
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    private void SetUpdateBusy(bool busy, bool resetDownloadUi = false)
    {
        if (busy) {
            _updateBusy = true;
            _updateCancellation = new CancellationTokenSource();
        } else {
            _updateCancellation?.Dispose();
            _updateCancellation = null;
            _updateBusy = false;
            if (resetDownloadUi) {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                CancelDownloadButton.Visibility = Visibility.Collapsed;
            }
        }
        UpdateUpdaterControls();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_preparedUpdate is null || _updateBusy || _exitInProgress) {
            return;
        }
        var message = _workerSnapshot.WorkerSnapshot?.ActiveRun is null
            ? "NarutoAutoGUI 将重启以完成更新。" : "当前任务将停止，NarutoAutoGUI 将重启以完成更新。";
        var dialog = new Wpf.Ui.Controls.MessageBox {
            Title = "安装更新？", Content = message, PrimaryButtonText = "安装并重启", CloseButtonText = "取消",
            Owner = this
        };
        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) {
            return;
        }
        UpdateDownloadStatus.Text = "正在准备安装…";
        try {
            await ((App)System.Windows.Application.Current).InstallUpdateAsync(_preparedUpdate);
        } catch (Exception exception) {
            _logger.Error("无法开始更新。", exception);
            if (exception is InvalidDataException) {
                var invalidStaging = _preparedUpdate.Staging;
                _preparedUpdate = null;
                try {
                    if (Directory.Exists(invalidStaging)) {
                        UpdatePackage.RejectLinks(invalidStaging);
                        Directory.Delete(invalidStaging, true);
                    }
                } catch (Exception cleanupError) {
                    _logger.Warn("废弃失效暂存包，清理未完成。", cleanupError);
                }
                UpdateDownloadStatus.Text = "更新包已失效，请重新下载。";
                UpdateUpdaterControls();
            } else {
                UpdateDownloadStatus.Text = "无法开始更新，请查看日志后重试。";
            }
        }
    }

    private void UpdateUpdaterControls()
    {
        var enabled = !_updateBusy && !_exitInProgress;
        CheckUpdateButton.IsEnabled = enabled && _preparedUpdate is null;
        DialogCheckUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled;
        CloseUpdateButton.IsEnabled = !_exitInProgress;
        DownloadUpdateButton.IsEnabled = enabled && _availableUpdate is not null;
        DownloadUpdateButton.Visibility = _preparedUpdate is null && _availableUpdate is not null
            ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = _preparedUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.IsEnabled = enabled;
        CancelDownloadButton.IsEnabled = !_exitInProgress;
        RefreshUpdatePresentation();
    }

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
        UpdateVersionText.Text = available ? _availableUpdate?.Tag : _updateSource?.Version ?? "";
        UpdateInstalledVersionText.Text = available ? $"当前版本：{_updateSource?.Version}" : "";
        UpdateInstalledVersionText.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateResultMessage.Text = _updateCheckState switch {
            UpdateCheckState.CheckFailed => UpdateCheckStatus.Text,
            UpdateCheckState.UpToDate => _updateCompletion is not null
                ? $"已成功更新到 {_updateCompletion.Tag}，感谢使用！" : "当前已是最新版本，感谢使用！",
            UpdateCheckState.UpdateAvailable => "",
            _ => _updateCompletion is not null ? $"已更新到 {_updateCompletion.Tag}" : "查看是否有可用的新版本。"
        };
        UpdateResultMessage.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        UpdateNotesPanel.Visibility = available || latest && _updateCompletion is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ReleaseNotesButton.Visibility = _updateBusy ? Visibility.Collapsed : Visibility.Visible;
        RefreshUpdateNotes();
    }
}
