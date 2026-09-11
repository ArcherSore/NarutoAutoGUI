using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using NarutoAutoGUI.Updates;

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
            if (_updateCompletion is not null) {
                UpdateBannerText.Text = $"已更新到 MaaNOP {_updateCompletion.Tag}";
                UpdateBanner.Visibility = Visibility.Visible;
            }
            if (StartupUpdateCheck.IsChecked == true) {
                _ = CheckUpdateAsync(automatic: true);
            }
        } catch (Exception exception) {
            _logger.Warn("更新初始化不可用。", exception);
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

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync(automatic: false);

    private async Task CheckUpdateAsync(bool automatic)
    {
        if (_updateBusy || _exitInProgress || _preparedUpdate is not null) {
            return;
        }
        SetUpdateBusy(true);
        var cancellation = _updateCancellation!.Token;
        if (!automatic) {
            UpdateCheckStatus.Text = "正在检查更新…";
        }
        try {
            _updateSource = UpdateSource.Load(AppContext.BaseDirectory);
            UpdateCurrentVersionText.Text = $"当前版本：{_updateSource.Version}";
            var service = new UpdateService(_updateClient, message => _logger.Info(message));
            _availableUpdate = await service.CheckAsync(_updateSource, cancellation);
            if (_availableUpdate is not null) {
                UpdateBannerText.Text = $"MaaNOP {_availableUpdate.Tag} 可用";
                UpdateBanner.Visibility = Visibility.Visible;
            } else if (_updateCompletion is null) {
                UpdateBanner.Visibility = Visibility.Collapsed;
            }
            if (!automatic) {
                UpdateCheckStatus.Text = _availableUpdate is null ? "当前已是最新版本" : "发现新版本，可查看更新。";
            }
            RefreshUpdateNotes();
        } catch (Exception exception) {
            _logger.Warn("检查更新失败。", exception);
            if (!automatic) {
                UpdateCheckStatus.Text = exception is InvalidDataException
                    ? exception.Message : "检查更新失败，请稍后重试。";
            }
        } finally {
            SetUpdateBusy(false);
        }
    }

    private void RefreshUpdateNotes()
    {
        UpdateVersionsText.Text = $"当前版本：{_updateSource?.Version}\n"
            + $"{(_availableUpdate is null ? "更新版本" : "最新版本")}：{_availableUpdate?.Tag ?? _updateCompletion?.Tag}";
        UpdateNotesText.Text = _availableUpdate?.Notes ?? _updateCompletion?.Notes ?? "";
    }

    private void ViewUpdate_Click(object sender, RoutedEventArgs e)
    {
        CloseTaskDescriptionDrawer();
        _updatePreviousFocus = Keyboard.FocusedElement;
        RefreshUpdateNotes();
        UpdateOverlay.Visibility = Visibility.Visible;
        CloseUpdateButton.Focus();
    }

    private void CloseUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
        if (_updatePreviousFocus is not null) {
            Keyboard.Focus(_updatePreviousFocus);
        }
    }

    private void UpdateOverlay_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) {
            CloseUpdate_Click(sender, e);
            e.Handled = true;
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
        DownloadUpdateButton.IsEnabled = enabled && _availableUpdate is not null;
        DownloadUpdateButton.Visibility = _preparedUpdate is null && _availableUpdate is not null
            ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = _preparedUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.IsEnabled = enabled;
    }
}
