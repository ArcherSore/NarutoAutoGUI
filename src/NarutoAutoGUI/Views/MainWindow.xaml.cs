using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using WpfBrush = System.Windows.Media.Brush;
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
    private ChildSessionSnapshot _sessionSnapshot = ChildSessionSnapshot.Empty;
    private ScrollViewer? _logScrollViewer;
    private bool _allowClose;
    private bool _busy;
    private bool _followLogs = true;
    private int _newLogCount;

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
        _sessionSnapshot = sessionManager.Snapshot;
        GamePathTextBox.Text = settings.GameExecutablePath;
        GameArgumentsTextBox.Text = settings.GameArguments;
        MaaNopPathTextBox.Text = settings.MaaNopExecutablePath;
        LogListBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogListBox_ScrollChanged));
        _logger.EntryWritten += OnLogEntryWritten;
        _sessionManager.StateChanged += OnSessionStateChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        UpdateCommandAvailability();
    }

    internal ObservableCollection<LogEntry> LogLines { get; } = [];

    internal event EventHandler? HiddenToTray;

    internal void AllowClose() => _allowClose = true;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _logScrollViewer = FindVisualChild<ScrollViewer>(LogListBox);
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
        HiddenToTray?.Invoke(this, EventArgs.Empty);
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
        => TryOpenLogsDirectory(showError: true);

    private bool TryOpenLogsDirectory(bool showError)
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
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("打开日志目录失败。", exception);
            OperationStatusText.Text = "失败：无法打开日志目录";
            if (showError)
            {
                ShowActionableError(
                    "打开日志目录失败",
                    exception,
                    "请确认 Windows 资源管理器可用，并检查日志目录访问权限后重试。",
                    offerLogDirectory: false);
            }

            return false;
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
            var operationName = status.TrimEnd('.', '…');
            if (operationName.StartsWith("正在", StringComparison.Ordinal))
            {
                operationName = operationName[2..];
            }

            HandleOperationError($"{operationName}失败", exception);
        }
        finally
        {
            SetBusy(false, OperationStatusText.Text);
        }
    }

    private void HandleOperationError(string operation, Exception exception)
    {
        _logger.Error($"{operation}。", exception);
        OperationStatusText.Text = $"失败：{operation}";
        ShowActionableError(
            operation,
            exception,
            GetRecoveryGuidance(operation),
            offerLogDirectory: true);
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
            OperationStatusText.Text = "失败：保存配置";
            if (showError)
            {
                ShowActionableError(
                    "保存配置失败",
                    exception,
                    "请确认程序目录可写，或将程序移动到有写入权限的目录后重试。",
                    offerLogDirectory: true);
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

        var shouldFollow = _followLogs && IsLogNearBottom();
        LogLines.Add(entry);
        while (LogLines.Count > MaximumGuiLogEntries)
        {
            LogLines.RemoveAt(0);
        }

        if (shouldFollow)
        {
            _newLogCount = 0;
            UpdateResumeLogFollowButton();
            _ = Dispatcher.BeginInvoke(
                ScrollLogsToEnd,
                DispatcherPriority.Background);
            return;
        }

        _followLogs = false;
        _newLogCount++;
        UpdateResumeLogFollowButton();
    }

    private void LogListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scrollViewer)
        {
            _logScrollViewer = scrollViewer;
        }

        if (e.VerticalChange < 0)
        {
            _followLogs = false;
            UpdateResumeLogFollowButton();
            return;
        }

        if (e.VerticalChange > 0 && IsLogNearBottom())
        {
            ResumeLogFollow(scrollToEnd: false);
        }
    }

    private void ResumeLogFollowButton_Click(object sender, RoutedEventArgs e) =>
        ResumeLogFollow(scrollToEnd: true);

    private void ResumeLogFollow(bool scrollToEnd)
    {
        _followLogs = true;
        _newLogCount = 0;
        UpdateResumeLogFollowButton();
        if (scrollToEnd)
        {
            ScrollLogsToEnd();
        }
    }

    private bool IsLogNearBottom()
    {
        _logScrollViewer ??= FindVisualChild<ScrollViewer>(LogListBox);
        return _logScrollViewer is null
               || _logScrollViewer.ScrollableHeight - _logScrollViewer.VerticalOffset <= 2.0;
    }

    private void ScrollLogsToEnd()
    {
        if (LogLines.LastOrDefault() is LogEntry lastLine)
        {
            LogListBox.ScrollIntoView(lastLine);
        }
    }

    private void UpdateResumeLogFollowButton()
    {
        ResumeLogFollowButton.Visibility = _followLogs || _newLogCount == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        ResumeLogFollowButton.Content = $"{_newLogCount} 条新日志，继续跟随(_F)";
        System.Windows.Automation.AutomationProperties.SetName(
            ResumeLogFollowButton,
            $"{_newLogCount} 条新日志，继续跟随");
    }

    private void OnSessionStateChanged(object? sender, ChildSessionSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnSessionStateChanged(sender, snapshot));
            return;
        }

        _sessionSnapshot = snapshot;
        UpdateSessionPresentation(snapshot);
        UpdateCommandAvailability();
    }

    private void UpdateSessionPresentation(ChildSessionSnapshot snapshot)
    {
        SessionStatusText.Text = GetStateText(snapshot.State);
        SessionDetailText.Text = GetStateDetail(snapshot.State);
        SessionIdText.Text = snapshot.ChildSessionId is uint id
            ? $"Session {id}  ·  RDP {snapshot.RdpConnectedState}"
            : $"Session —  ·  RDP {snapshot.RdpConnectedState}";
        SessionStatusBadgeText.Text = GetStateBadgeText(snapshot.State);

        var (surfaceKey, borderKey, foregroundKey, indicatorKey) = snapshot.State switch
        {
            ChildSessionState.ConnectedVisible => (
                "Brush.Success.Surface",
                "Brush.Success.Border",
                "Brush.Success.Foreground",
                "Brush.Success"),
            ChildSessionState.Disconnecting => (
                "Brush.Warning.Surface",
                "Brush.Warning.Border",
                "Brush.Warning.Foreground",
                "Brush.Warning"),
            ChildSessionState.Faulted => (
                "Brush.Error.Surface",
                "Brush.Error.Border",
                "Brush.Error.Foreground",
                "Brush.Error"),
            ChildSessionState.NotRunning => (
                "Brush.Surface.Disabled",
                "Brush.Border",
                "Brush.Text.Secondary",
                "Brush.Text.Muted"),
            _ => (
                "Brush.Info.Surface",
                "Brush.Primary.Border",
                "Brush.Info.Foreground",
                "Brush.Primary")
        };

        SessionStatusBadge.Background = (WpfBrush)FindResource(surfaceKey);
        SessionStatusBadge.BorderBrush = (WpfBrush)FindResource(borderKey);
        SessionStatusBadgeText.Foreground = (WpfBrush)FindResource(foregroundKey);
        SessionStatusIndicator.Fill = (WpfBrush)FindResource(indicatorKey);
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        OperationStatusText.Text = status;
        OperationProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
        UpdateCommandAvailability();
    }

    private void UpdateCommandAvailability()
    {
        var state = _sessionSnapshot.State;
        var canStartCommand = !_busy
                              && state is not ChildSessionState.Connecting
                              && state is not ChildSessionState.Disconnecting;

        CreateSessionButton.IsEnabled = canStartCommand
                                        && state is ChildSessionState.NotRunning
                                            or ChildSessionState.Existing
                                            or ChildSessionState.Faulted;
        ShowSessionButton.IsEnabled = canStartCommand
                                      && state == ChildSessionState.ConnectedHidden;
        HideSessionButton.IsEnabled = canStartCommand
                                      && state == ChildSessionState.ConnectedVisible;
        TerminateSessionButton.IsEnabled = canStartCommand
                                           && _sessionSnapshot.ChildSessionId is not null;

        // Launch commands intentionally remain available without a Session because they create
        // or restore it through the existing EnsureConnectedAsync workflow.
        LaunchGameButton.IsEnabled = canStartCommand;
        LaunchMaaNopButton.IsEnabled = canStartCommand;
        LaunchAllButton.IsEnabled = canStartCommand;
    }

    private static string GetStateText(ChildSessionState state) => state switch
    {
        ChildSessionState.NotRunning => "桌面分身未运行",
        ChildSessionState.Existing => "检测到已有桌面分身",
        ChildSessionState.Connecting => "正在连接桌面分身…",
        ChildSessionState.ConnectedVisible => "已连接 · 子桌面可见",
        ChildSessionState.ConnectedHidden => "已连接 · 子桌面已隐藏",
        ChildSessionState.Disconnecting => "正在结束桌面分身…",
        ChildSessionState.Faulted => "桌面分身连接失败",
        _ => state.ToString()
    };

    private static string GetStateDetail(ChildSessionState state) => state switch
    {
        ChildSessionState.NotRunning => "创建或一键启动时将自动建立桌面分身。",
        ChildSessionState.Existing => "可以恢复已有桌面分身的连接。",
        ChildSessionState.Connecting => "正在建立 RDP 连接，请稍候。",
        ChildSessionState.ConnectedVisible => "子桌面窗口可见，连接保持活动。",
        ChildSessionState.ConnectedHidden => "窗口已隐藏，子桌面中的程序仍在运行。",
        ChildSessionState.Disconnecting => "正在注销 Session 并清理连接。",
        ChildSessionState.Faulted => "请检查管理员权限和系统状态后重试；详细信息已写入日志。",
        _ => string.Empty
    };

    private static string GetStateBadgeText(ChildSessionState state) => state switch
    {
        ChildSessionState.NotRunning => "未运行",
        ChildSessionState.Existing => "已检测",
        ChildSessionState.Connecting => "连接中",
        ChildSessionState.ConnectedVisible => "可见",
        ChildSessionState.ConnectedHidden => "已隐藏",
        ChildSessionState.Disconnecting => "正在结束",
        ChildSessionState.Faulted => "连接失败",
        _ => "未知状态"
    };

    private static string GetRecoveryGuidance(string operation)
    {
        if (operation.Contains("启动", StringComparison.Ordinal))
        {
            return "请确认程序路径和启动参数正确，并检查桌面分身连接状态后重试。";
        }

        if (operation.Contains("桌面分身", StringComparison.Ordinal)
            || operation.Contains("子桌面", StringComparison.Ordinal))
        {
            return "请确认程序以管理员权限运行，并检查桌面分身状态后重试。";
        }

        return "请检查当前配置和系统状态后重试。";
    }

    private void ShowActionableError(
        string title,
        Exception exception,
        string recovery,
        bool offerLogDirectory)
    {
        var message = $"{exception.GetBaseException().Message}\n\n{recovery}";
        if (!offerLogDirectory)
        {
            WpfMessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var answer = WpfMessageBox.Show(
            $"{message}\n\n是否打开日志目录查看详细信息？",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Error,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            TryOpenLogsDirectory(showError: true);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
            {
                return result;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
