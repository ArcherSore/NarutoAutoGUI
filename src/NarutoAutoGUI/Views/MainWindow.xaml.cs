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
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;
using WpfBrush = System.Windows.Media.Brush;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfOpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace NarutoAutoGUI.Views;

public partial class MainWindow : Window
{
    private const int MaximumGuiLogEntries = 1000;
    private readonly AppLogger _logger;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ChildSessionManager _sessionManager;
    private readonly ChildSessionProgramService _programService;
    private readonly WorkerCoordinator _workerCoordinator;
    private readonly Func<Func<Task>, Task> _runApplicationOperationAsync;
    private readonly Func<Task> _requestExitAsync;
    private ChildSessionSnapshot _sessionSnapshot = ChildSessionSnapshot.Empty;
    private WorkerCoordinatorSnapshot _workerSnapshot = WorkerCoordinatorSnapshot.Empty;
    private ProjectPlanModule? _projectPlan;
    private RunStartAttempt? _pendingStartAttempt;
    private ScrollViewer? _logScrollViewer;
    private bool _allowClose;
    private bool _busy;
    private bool _exitInProgress;
    private bool _followLogs = true;
    private bool _updatingTaskSelection;
    private int _newLogCount;

    internal MainWindow(
        AppLogger logger,
        AppSettingsStore settingsStore,
        AppSettings settings,
        ChildSessionManager sessionManager,
        ChildSessionProgramService programService,
        WorkerCoordinator workerCoordinator,
        Func<Func<Task>, Task> runApplicationOperationAsync,
        Func<Task> requestExitAsync)
    {
        InitializeComponent();
        DataContext = this;
        _logger = logger;
        _settingsStore = settingsStore;
        _settings = settings;
        _sessionManager = sessionManager;
        _programService = programService;
        _workerCoordinator = workerCoordinator;
        _runApplicationOperationAsync = runApplicationOperationAsync;
        _requestExitAsync = requestExitAsync;
        _sessionSnapshot = sessionManager.Snapshot;
        GamePathTextBox.Text = settings.GameExecutablePath;
        GameArgumentsTextBox.Text = settings.GameArguments;
        MaaNopProjectDirectoryTextBox.Text = settings.MaaNopProjectDirectory;
        LogListBox.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(LogListBox_ScrollChanged));
        _logger.EntryWritten += OnLogEntryWritten;
        _sessionManager.StateChanged += OnSessionStateChanged;
        _workerCoordinator.StateChanged += OnWorkerStateChanged;
        _workerCoordinator.LogReceived += OnWorkerLogReceived;
        _workerSnapshot = workerCoordinator.Snapshot;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        UpdateCommandAvailability();
    }

    internal ObservableCollection<LogEntry> LogLines { get; } = [];

    internal event EventHandler? HiddenToTray;

    internal void AllowClose() => _allowClose = true;

    internal void SetExitInProgress(bool exitInProgress)
    {
        _exitInProgress = exitInProgress;
        UpdateCommandAvailability();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _logScrollViewer = FindVisualChild<ScrollViewer>(LogListBox);
        TryLoadProject(showError: !string.IsNullOrWhiteSpace(MaaNopProjectDirectoryTextBox.Text));
        UpdateWorkerPresentation(_workerSnapshot);
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
            _workerCoordinator.StateChanged -= OnWorkerStateChanged;
            _workerCoordinator.LogReceived -= OnWorkerLogReceived;
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
        if (_exitInProgress)
        {
            return;
        }

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

        await RunOperationAsync(
            "正在结束桌面分身...",
            async () =>
            {
                await _sessionManager.TerminateAsync();
                _workerCoordinator.ChildSessionEnded();
            });
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

    private void BrowseMaaNopProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseProjectDirectory(
            MaaNopProjectDirectoryTextBox.Text,
            "选择直接包含 interface.json 的 MaaNOP Project Directory");
        if (path is not null)
        {
            MaaNopProjectDirectoryTextBox.Text = path;
            if (TrySaveSettings(showError: true))
            {
                TryLoadProject(showError: true);
            }
        }
    }

    private async void LaunchGameButton_Click(object sender, RoutedEventArgs e) =>
        await LaunchSingleAsync(
            "游戏",
            GamePathTextBox.Text,
            GameArgumentsTextBox.Text);

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

    private async void PrepareEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            "正在准备真实 E2E 环境...",
            async () =>
            {
                SaveSettings();
                LoadProject();
                var sessionId = await _sessionManager.EnsureConnectedAsync(showPreview: true);
                await _workerCoordinator.PrepareWorkerAsync(
                    sessionId,
                    _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"));
                await _programService.LaunchIfNeededAsync(
                    sessionId,
                    GamePathTextBox.Text,
                    GameArgumentsTextBox.Text);
                _sessionManager.ShowPreview();
                _logger.Info("真实 E2E 环境已准备；请在子桌面完成人工登录后隐藏子桌面。 ");
            });
    }

    private async void PrepareWorkerButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            "正在准备 Child Session Worker...",
            async () =>
            {
                SaveSettings();
                LoadProject();
                var sessionId = await _sessionManager.EnsureConnectedAsync(showPreview: true);
                await _workerCoordinator.PrepareWorkerAsync(
                    sessionId,
                    _projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"));
            });
    }

    private async void StartRunButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            "正在提交真实 MaaNOP Run...",
            async () =>
            {
                if (_sessionSnapshot.State != ChildSessionState.ConnectedHidden)
                {
                    throw new InvalidOperationException("首片要求先隐藏 Child Session，再从主桌面启动 Run。 ");
                }
                var project = _projectPlan
                              ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。 ");
                _pendingStartAttempt ??= project.CreateRunStartAttempt();
                var response = await _workerCoordinator.StartRunAsync(_pendingStartAttempt);
                if (response.Disposition is not ("accepted" or "already_accepted"))
                {
                    throw new InvalidDataException($"未知 run.start disposition：{response.Disposition}。 ");
                }
                _logger.Info(
                    $"run.start {response.Disposition}：runId={_pendingStartAttempt.RunId:D}；"
                    + $"planDigest={_pendingStartAttempt.PlanDigest}。 ");
                _pendingStartAttempt = null;
            });
    }

    private async void StopRunButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            "正在请求停止真实 MaaNOP Run...",
            async () =>
            {
                var activeRun = _workerSnapshot.WorkerSnapshot?.ActiveRun
                                ?? throw new InvalidOperationException("Worker 当前没有 active Run。 ");
                if (activeRun.State != RunState.Running
                    || activeRun.Items.Single().State != PlanItemState.Running)
                {
                    throw new InvalidOperationException(
                        "首片取消验收必须先确认 Run 与唯一 Plan Item 都已进入 Running。 ");
                }
                var response = await _workerCoordinator.StopRunAsync(activeRun.RunId);
                if (response.Disposition != "stop_requested")
                {
                    throw new InvalidDataException(
                        $"首次 run.stop 未返回 stop_requested：{response.Disposition}。 ");
                }
                _logger.Info($"run.stop 已确认 stop_requested：runId={activeRun.RunId:D}。 ");
                OperationStatusText.Text = "停止请求已接受，正在等待 MaaFramework Stop 与清理确认";
            });
    }

    private void MaaNopTaskComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTaskSelection || MaaNopTaskComboBox.SelectedItem is not ProjectTaskChoice task)
        {
            return;
        }
        try
        {
            (_projectPlan ?? throw new InvalidOperationException("MaaNOP 项目尚未加载。"))
                .SelectTask(task.Name);
            _pendingStartAttempt = null;
            _logger.Info($"已保存 MaaNOP Config：SelectedTasks=[{task.Name}]，ExplicitOptions={{}}。 ");
            UpdateCommandAvailability();
        }
        catch (Exception exception)
        {
            HandleOperationError("保存 MaaNOP task 选择失败", exception);
        }
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

    private async Task RunOperationAsync(string status, Func<Task> operation)
    {
        if (_busy || _exitInProgress)
        {
            return;
        }

        SetBusy(true, status);
        try
        {
            await _runApplicationOperationAsync(operation);
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
        _settings.MaaNopProjectDirectory = MaaNopProjectDirectoryTextBox.Text.Trim();
        _settingsStore.Save(_settings);
        MaaNopProjectDirectoryTextBox.Text = _settings.MaaNopProjectDirectory;
    }

    private void PathsTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        TrySaveSettings(showError: true);

    private void MaaNopProjectDirectoryTextBox_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (TrySaveSettings(showError: true))
        {
            TryLoadProject(showError: true);
        }
    }

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

    private void LoadProject()
    {
        var projectDirectory = MaaNopProjectDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new InvalidOperationException("请选择 MaaNOP Project Directory。 ");
        }

        var project = ProjectPlanModule.Open(projectDirectory, _settingsStore.MaaNopConfigPath);
        _projectPlan = project;
        _pendingStartAttempt = null;
        _updatingTaskSelection = true;
        try
        {
            MaaNopTaskComboBox.ItemsSource = project.Tasks;
            MaaNopTaskComboBox.SelectedItem = project.SelectedTaskName is null
                ? null
                : project.Tasks.Single(task => task.Name == project.SelectedTaskName);
        }
        finally
        {
            _updatingTaskSelection = false;
        }

        ProjectStatusText.Text =
            $"{project.ProjectName} {project.ProjectVersion} · {project.Tasks.Count} 个 top-level task";
        var invalidTasks = project.Tasks.Where(task => !task.DefaultOnlyValid).ToArray();
        if (invalidTasks.Length == 0)
        {
            ProjectValidationText.Text = "所有 task 均可经正式 PI Resolver 使用纯默认配置构造 Run Plan。";
        }
        else
        {
            ProjectValidationText.Text = string.Join(
                "；",
                invalidTasks.Select(task => $"{task.Name}: {task.ValidationError}"));
        }
        _logger.Info(
            $"已加载 MaaNOP Project Interface：{project.ProjectName} {project.ProjectVersion}；"
            + $"interfaceDigest={project.SourceInterfaceDigest}；"
            + $"runtimeProfileDigest={project.RuntimeProfileDigest}。 ");
        UpdateCommandAvailability();
    }

    private bool TryLoadProject(bool showError)
    {
        try
        {
            LoadProject();
            return true;
        }
        catch (Exception exception)
        {
            _projectPlan = null;
            _pendingStartAttempt = null;
            MaaNopTaskComboBox.ItemsSource = null;
            ProjectStatusText.Text = "MaaNOP 项目未就绪";
            ProjectValidationText.Text = exception.GetBaseException().Message;
            _logger.Warn("加载 MaaNOP Project Interface 失败。", exception);
            UpdateCommandAvailability();
            if (showError)
            {
                ShowActionableError(
                    "加载 MaaNOP 项目失败",
                    exception,
                    "请选择直接包含真实 interface.json、agent 和 resource 的 MaaNOP Project Directory。",
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

    private static string? BrowseProjectDirectory(string currentPath, string title)
    {
        var dialog = new WpfOpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = Path.GetFullPath(currentPath);
        }
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
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

    private void OnWorkerStateChanged(object? sender, WorkerCoordinatorSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnWorkerStateChanged(sender, snapshot));
            return;
        }

        _workerSnapshot = snapshot;
        UpdateWorkerPresentation(snapshot);
        UpdateCommandAvailability();
    }

    private void OnWorkerLogReceived(object? sender, WorkerLogEntry entry)
    {
        var message = $"Worker #{entry.Sequence} [{entry.Source}] {entry.Message}";
        switch (entry.Level.ToLowerInvariant())
        {
            case "critical":
                _logger.Critical(message);
                break;
            case "error":
                _logger.Error(message);
                break;
            case "warning":
            case "warn":
                _logger.Warn(message);
                break;
            case "debug":
                _logger.Debug(message);
                break;
            default:
                _logger.Info(message);
                break;
        }
    }

    private void UpdateWorkerPresentation(WorkerCoordinatorSnapshot snapshot)
    {
        WorkerObservationText.Text = snapshot.Observation.ToString();
        WorkerDetailText.Text = snapshot.Detail;
        var worker = snapshot.WorkerSnapshot;
        if (worker is null)
        {
            WorkerStateText.Text = "Worker — · Snapshot stale";
            DependencyStatusText.Text = "依赖尚未探测";
            RunStatusText.Text = "Run — · Plan Item —";
            return;
        }

        WorkerStateText.Text =
            $"Worker {worker.WorkerState} · PID {worker.WorkerPid} · Snapshot r{worker.StateRevision} "
            + (snapshot.SnapshotFresh ? "fresh" : "stale");
        var dependencies = worker.DependencyStatus;
        DependencyStatusText.Text =
            $"Maa.Binding {dependencies.MaaFrameworkBindingVersion} · Maa.Runtime {dependencies.MaaFrameworkRuntimeVersion} · "
            + $"Python {(dependencies.Python.Success ? "Ready" : "Failed")}: "
            + $"{dependencies.Python.Value ?? dependencies.Python.Error}";

        var run = worker.ActiveRun ?? worker.LastRun;
        if (run is null)
        {
            RunStatusText.Text = "Run Idle · activeRun=null · lastRun=null";
            return;
        }
        var item = run.Items.SingleOrDefault();
        var slot = worker.ActiveRun is null ? "lastRun" : "activeRun";
        RunStatusText.Text =
            $"{slot} {run.State} · {item?.TaskLabel ?? "—"} = {item?.State.ToString() ?? "—"} · "
            + $"runId={run.RunId:D}";
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
                              && !_exitInProgress
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

        var projectReady = _projectPlan is not null;
        var worker = _workerSnapshot.WorkerSnapshot;
        var workerIdleFresh = _workerSnapshot.Observation == WorkerObservation.Connected
                              && _workerSnapshot.SnapshotFresh
                              && worker is not null
                              && worker.ActiveRun is null
                              && worker.RunState == RunState.Idle;
        var canEditProject = canStartCommand
                             && (_workerSnapshot.Observation is WorkerObservation.WorkerNotStarted
                                     or WorkerObservation.ChildSessionEnded
                                 || workerIdleFresh);

        // Game launch remains available without a Session because it uses the frozen
        // EnsureConnectedAsync + Task Scheduler flow.
        LaunchGameButton.IsEnabled = canStartCommand;
        MaaNopProjectDirectoryTextBox.IsEnabled = canEditProject;
        BrowseMaaNopProjectButton.IsEnabled = canEditProject;
        MaaNopTaskComboBox.IsEnabled = canEditProject && projectReady;
        PrepareWorkerButton.IsEnabled = canStartCommand
                                        && projectReady
                                        && _workerSnapshot.Observation is WorkerObservation.WorkerNotStarted
                                            or WorkerObservation.ChildSessionEnded;
        PrepareEnvironmentButton.IsEnabled = canStartCommand && projectReady;

        var selectedTaskValid = _projectPlan?.Tasks.SingleOrDefault(
            task => task.Name == _projectPlan.SelectedTaskName)?.DefaultOnlyValid == true;
        StartRunButton.IsEnabled = canStartCommand
                                   && _sessionSnapshot.State == ChildSessionState.ConnectedHidden
                                   && workerIdleFresh
                                   && worker!.WorkerState == WorkerState.Ready
                                   && projectReady
                                   && selectedTaskValid
                                   && worker.RuntimeProfileDigest == _projectPlan!.RuntimeProfileDigest;
        var active = worker?.ActiveRun;
        StopRunButton.IsEnabled = canStartCommand
                                  && _workerSnapshot.Observation == WorkerObservation.Connected
                                  && _workerSnapshot.SnapshotFresh
                                  && active?.State == RunState.Running
                                  && active.Items.Count == 1
                                  && active.Items[0].State == PlanItemState.Running;
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
