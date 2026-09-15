using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using NarutoAutoGUI.Updates;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private CancellationTokenSource? _updateCancellation;
    private EngineCheckResult? _updateCheck;
    private bool _updateBusy;
    private IInputElement? _updatePreviousFocus;

    private static string UpdatePreferencePath => Path.Combine(AppContext.BaseDirectory, "config", "update-check.txt");

    private void InitializeUpdates()
    {
        UpdateCurrentVersionText.Text = $"当前版本：{_projectPlan?.ProjectVersion ?? "—"}";
        try {
            StartupUpdateCheck.IsChecked = !File.Exists(UpdatePreferencePath)
                || File.ReadAllText(UpdatePreferencePath) != "false";
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
            Directory.CreateDirectory(Path.GetDirectoryName(UpdatePreferencePath)!);
            File.WriteAllText(UpdatePreferencePath, StartupUpdateCheck.IsChecked == true ? "true" : "false");
        } catch (Exception exception) {
            UpdateCheckStatus.Text = "无法保存更新设置，请重试。";
            _logger.Warn("保存更新设置失败。", exception);
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync(automatic: false);

    private async Task CheckUpdateAsync(bool automatic)
    {
        if (_updateBusy || _exitInProgress) {
            return;
        }
        _updateBusy = true;
        _updateCancellation = new CancellationTokenSource();
        _updateCheck = null;
        UpdateBanner.Visibility = Visibility.Collapsed;
        RefreshUpdateNotes();
        UpdateUpdaterControls();
        if (!automatic) {
            UpdateCheckStatus.Text = "正在检查更新…";
        }
        try {
            var executable = Path.Combine(AppContext.BaseDirectory, "maanop-update-engine.exe");
            var engine = new UpdateEngineClient(new ProcessStartInfo(executable) {
                WorkingDirectory = AppContext.BaseDirectory
            });
            var result = await engine.CheckAsync(AppContext.BaseDirectory, _updateCancellation.Token);
            if (_exitInProgress) {
                return;
            }
            _updateCheck = result;
            UpdateCurrentVersionText.Text = $"当前版本：{result.CurrentVersion}";
            if (result.Update is not null) {
                UpdateBannerText.Text = $"MaaNOP {result.Update.Version} 可用";
                UpdateBanner.Visibility = Visibility.Visible;
            }
            if (!automatic) {
                UpdateCheckStatus.Text = result.Update is null ? "当前已是最新版本" : "发现新版本，可查看更新。";
            }
            RefreshUpdateNotes();
        } catch (OperationCanceledException) when (_exitInProgress) {
            // Exiting cancels this optional operation; it does not alter the runtime shutdown path.
        } catch (Exception exception) {
            _logger.Warn("检查更新失败。", exception);
            if (!automatic && !_exitInProgress) {
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
        UpdateVersionsText.Text = $"当前版本：{_updateCheck?.CurrentVersion ?? _projectPlan?.ProjectVersion ?? "—"}\n"
            + $"最新版本：{_updateCheck?.Update?.Version ?? "—"}";
        UpdateNotesText.Text = _updateCheck?.Update?.Notes ?? "";
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

    private void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
    }

    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    private void UpdateUpdaterControls()
    {
        CheckUpdateButton.IsEnabled = !_updateBusy && !_exitInProgress;
        // Ticket 01 cannot send a Rust descriptor to the old C# prepare/install chain.
        DownloadUpdateButton.IsEnabled = false;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        CancelDownloadButton.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        UpdateDownloadStatus.Text = _updateCheck?.Update is not null
            ? "此开发版本暂不支持下载和安装更新。" : "";
    }
}
