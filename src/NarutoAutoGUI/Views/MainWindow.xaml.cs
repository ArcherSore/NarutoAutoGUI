using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace NarutoAutoGUI.Views;

public partial class MainWindow : Window
{
    private const int MaximumGuiLogEntries = 1000;
    private readonly AppLogger _logger;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ChildSessionManager _sessionManager;
    private readonly ChildSessionProgramService _programService;
    private readonly Func<Task> _requestExitAsync;
    private bool _allowClose;
    private bool _busy;

    internal MainWindow(
        AppLogger logger,
        AppSettingsStore settingsStore,
        AppSettings settings,
        ChildSessionManager sessionManager,
        ChildSessionProgramService programService,
        Func<Task> requestExitAsync)
    {
        InitializeComponent();
        DataContext = this;
        _logger = logger;
        _settingsStore = settingsStore;
        _settings = settings;
        _sessionManager = sessionManager;
        _programService = programService;
        _requestExitAsync = requestExitAsync;
        GamePathTextBox.Text = settings.GameExecutablePath;
        GameArgumentsTextBox.Text = settings.GameArguments;
        MaaNopPathTextBox.Text = settings.MaaNopExecutablePath;
        _logger.EntryWritten += OnLogEntryWritten;
        _sessionManager.StateChanged += OnSessionStateChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    internal ObservableCollection<string> LogLines { get; } = [];

    internal void AllowClose() => _allowClose = true;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        var existingId = _sessionManager.DetectExistingSession();
        if (existingId is null)
        {
            return;
        }

        await RunOperationAsync(
            "正在恢复已有桌面分身...",
            async () =>
            {
                await _sessionManager.EnsureConnectedAsync(showPreview: true);
                _logger.Info($"已有 Child Session {existingId} 恢复完成。");
            });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _logger.EntryWritten -= OnLogEntryWritten;
            _sessionManager.StateChanged -= OnSessionStateChanged;
            return;
        }

        e.Cancel = true;
        TrySaveSettings(showError: false);
        Hide();
        _logger.Info("主窗口已隐藏到托盘。");
    }

    private async void CreateSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(
            "正在创建或恢复桌面分身...",
            async () => await _sessionManager.EnsureConnectedAsync(showPreview: true));

    private async void ShowSessionButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(
            "正在显示子桌面...",
            async () => await _sessionManager.EnsureConnectedAsync(showPreview: true));

    private void HideSessionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _sessionManager.HidePreview();
            OperationStatusText.Text = "子桌面已隐藏，连接保持存活";
        }
        catch (Exception exception)
        {
            HandleOperationError("隐藏子桌面失败", exception);
        }
    }

    private async void TerminateSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = WpfMessageBox.Show(
            "结束桌面分身将注销 Session，并结束其中运行的程序。确认继续吗？",
            "结束桌面分身",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync("正在结束桌面分身...", () => _sessionManager.TerminateAsync());
    }

    private void BrowseGameButton_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseExecutable(GamePathTextBox.Text, "选择游戏程序");
        if (path is not null)
        {
            GamePathTextBox.Text = path;
            TrySaveSettings(showError: true);
        }
    }

    private void BrowseMaaNopButton_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseExecutable(MaaNopPathTextBox.Text, "选择 MaaNOP 程序");
        if (path is not null)
        {
            MaaNopPathTextBox.Text = path;
            TrySaveSettings(showError: true);
        }
    }

    private async void LaunchGameButton_Click(object sender, RoutedEventArgs e) =>
        await LaunchSingleAsync(
            "游戏",
            GamePathTextBox.Text,
            GameArgumentsTextBox.Text);

    private async void LaunchMaaNopButton_Click(object sender, RoutedEventArgs e) =>
        await LaunchSingleAsync("MaaNOP", MaaNopPathTextBox.Text);

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_logger.LogDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            HandleOperationError("打开日志目录失败", exception);
        }
    }

    private async void LaunchAllButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            "正在启动挂机环境...",
            async () =>
            {
                SaveSettings();
                var sessionId = await _sessionManager.EnsureConnectedAsync(showPreview: true);
                var failures = new List<string>();

                await TryLaunchTargetAsync(
                    "游戏",
                    GamePathTextBox.Text,
                    GameArgumentsTextBox.Text,
                    sessionId,
                    failures);
                await TryLaunchTargetAsync(
                    "MaaNOP",
                    MaaNopPathTextBox.Text,
                    arguments: string.Empty,
                    sessionId,
                    failures);
                _sessionManager.ShowPreview();

                if (failures.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
                }

                _logger.Info("挂机环境启动完成，子桌面保持显示。");
            });
    }

    private async Task LaunchSingleAsync(
        string displayName,
        string path,
        string arguments = "")
    {
        await RunOperationAsync(
            $"正在启动{displayName}...",
            async () =>
            {
                SaveSettings();
                var sessionId = await _sessionManager.EnsureConnectedAsync(showPreview: true);
                await _programService.LaunchIfNeededAsync(
                    sessionId,
                    path,
                    arguments);
            });
    }

    private async Task TryLaunchTargetAsync(
        string displayName,
        string path,
        string arguments,
        uint sessionId,
        ICollection<string> failures)
    {
        try
        {
            await _programService.LaunchIfNeededAsync(
                sessionId,
                path,
                arguments);
        }
        catch (Exception exception)
        {
            var message = $"{displayName}启动失败：{exception.GetBaseException().Message}";
            failures.Add(message);
            _logger.Error(message, exception);
        }
    }

    private async Task RunOperationAsync(string status, Func<Task> operation)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, status);
        try
        {
            await operation();
            OperationStatusText.Text = "操作完成";
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"操作已取消：{status}");
            OperationStatusText.Text = "操作已取消";
        }
        catch (Exception exception)
        {
            HandleOperationError(status.TrimEnd('.', '…'), exception);
        }
        finally
        {
            SetBusy(false, OperationStatusText.Text);
        }
    }

    private void HandleOperationError(string operation, Exception exception)
    {
        _logger.Error($"{operation}。", exception);
        OperationStatusText.Text = $"失败：{exception.GetBaseException().Message}";
        WpfMessageBox.Show(
            exception.GetBaseException().Message,
            operation,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void SaveSettings()
    {
        _settings.GameExecutablePath = GamePathTextBox.Text.Trim();
        _settings.GameArguments = GameArgumentsTextBox.Text.Trim();
        _settings.MaaNopExecutablePath = MaaNopPathTextBox.Text.Trim();
        _settingsStore.Save(_settings);
    }

    private void PathsTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        TrySaveSettings(showError: true);

    private bool TrySaveSettings(bool showError)
    {
        try
        {
            SaveSettings();
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("保存程序路径配置失败。", exception);
            OperationStatusText.Text = $"保存配置失败：{exception.GetBaseException().Message}";
            if (showError)
            {
                WpfMessageBox.Show(
                    exception.GetBaseException().Message,
                    "保存配置失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }
    }

    private static string? BrowseExecutable(string currentPath, string title)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = title,
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            try
            {
                dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(currentPath));
                dialog.FileName = Path.GetFileName(currentPath);
            }
            catch
            {
                // Ignore malformed current text; the dialog remains usable.
            }
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OnLogEntryWritten(object? sender, LogEntry entry)
    {
        if (entry.Level < LogLevel.Info)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnLogEntryWritten(sender, entry));
            return;
        }

        LogLines.Add(entry.ToString());
        while (LogLines.Count > MaximumGuiLogEntries)
        {
            LogLines.RemoveAt(0);
        }

        LogListBox.ScrollIntoView(LogLines.LastOrDefault());
    }

    private void OnSessionStateChanged(object? sender, ChildSessionSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnSessionStateChanged(sender, snapshot));
            return;
        }

        SessionStatusText.Text = $"{GetStateText(snapshot.State)} · {snapshot.Detail} · RDP={snapshot.RdpConnectedState}";
        SessionIdText.Text = snapshot.ChildSessionId is uint id
            ? $"childSessionId: {id}"
            : "childSessionId: —";
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        OperationStatusText.Text = status;
        CreateSessionButton.IsEnabled = !busy;
        ShowSessionButton.IsEnabled = !busy;
        HideSessionButton.IsEnabled = !busy;
        TerminateSessionButton.IsEnabled = !busy;
        LaunchGameButton.IsEnabled = !busy;
        LaunchMaaNopButton.IsEnabled = !busy;
        LaunchAllButton.IsEnabled = !busy;
    }

    private static string GetStateText(ChildSessionState state) => state switch
    {
        ChildSessionState.NotRunning => "未运行",
        ChildSessionState.Existing => "已检测",
        ChildSessionState.Connecting => "连接中",
        ChildSessionState.ConnectedVisible => "已连接",
        ChildSessionState.ConnectedHidden => "已连接",
        ChildSessionState.Disconnecting => "正在结束",
        ChildSessionState.Faulted => "异常",
        _ => state.ToString()
    };
}
