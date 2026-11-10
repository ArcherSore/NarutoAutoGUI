using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace NarutoAutoGUI;

public partial class App : System.Windows.Application
{
    private readonly RunTerminalNotificationTracker _terminalNotifications = new();
    private AppLogger? _logger;
    private ChildSessionManager? _sessionManager;
    private WorkerCoordinator? _workerCoordinator;
    private RuntimeEnvironment? _environment;
    private MainWindow? _mainWindow;
    private readonly GuiTelemetry _telemetry = new();
    private Forms.NotifyIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _hasShownTrayNotification;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) {
            Environment.ExitCode = SelfTestRunner.Run(e.Args);
            Shutdown(Environment.ExitCode);
            return;
        }

        if (!TryAcquireSingleInstance()) {
            Shutdown(Environment.ExitCode);
            return;
        }

        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        _logger = new AppLogger();
        RegisterGlobalExceptionHandlers(_logger);
        // The SDK reports AppDomain and unobserved task exceptions itself once the loaded project enables it.
        _telemetry.Attach(Dispatcher);
        LogStartup(_logger);

        _sessionManager = new ChildSessionManager(_logger);
        _workerCoordinator = new WorkerCoordinator(
            _logger, Path.Combine(AppContext.BaseDirectory, "state"),
            Path.Combine(AppContext.BaseDirectory, "worker", "NarutoAutoWorker.exe"));
        _workerCoordinator.StateChanged += WorkerCoordinator_StateChanged;
        _environment = new RuntimeEnvironment(_logger, _sessionManager, _workerCoordinator);
        _mainWindow = new MainWindow(
            _logger, _sessionManager, _workerCoordinator, _environment, RequestExitAsync, EndEnvironmentAsync,
            InstallUpdateAsync, telemetry: _telemetry);
        _mainWindow.HiddenToTray += MainWindow_HiddenToTray;
        _mainWindow.ProjectIdentityChanged += MainWindow_ProjectIdentityChanged;
        MainWindow = _mainWindow;
        CreateTrayIcon();
        _mainWindow.Show();
    }

    internal static string ApplicationVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";

    internal static void LogStartup(AppLogger logger)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        logger.Info($"NarutoAutoGUI 正式 GUI 启动 [version={ApplicationVersion}] [pid={process.Id}] "
            + $"[session={process.SessionId}] [elevated={elevated}] [os={Environment.OSVersion}] "
            + $"[arch={RuntimeInformation.ProcessArchitecture}] [runtime={RuntimeInformation.FrameworkDescription}] "
            + $"[directory={AppContext.BaseDirectory}] [logDirectory={logger.LogDirectory}]");
    }

    internal async Task RequestExitAsync()
    {
        if (IsExiting || _logger is null || _sessionManager is null || _mainWindow is null || _environment is null) {
            return;
        }

        _environment.Closing = true;
        _mainWindow.SetExitInProgress(true);
        var exitCompleted = false;
        try {
            await _environment.EndAsync(ConfirmExit, async ended => {
                if (ended.Outcome != EnvironmentEndOutcome.Ended) {
                    if (ended.Error is { } error) {
                        WpfMessageBox.Show(
                            $"{error.Message}已取消退出，避免遗留未知状态。请查看日志后重试。",
                            "NarutoAutoGUI",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    return;
                }

                _logger.Info("NarutoAutoGUI 正常退出。");
                _trayIcon?.Dispose();
                _trayIcon = null;
                _sessionManager.Dispose();
                if (_workerCoordinator is not null) {
                    _workerCoordinator.StateChanged -= WorkerCoordinator_StateChanged;
                    await _workerCoordinator.DisposeAsync();
                    _workerCoordinator = null;
                }
                _mainWindow.HiddenToTray -= MainWindow_HiddenToTray;
                _mainWindow.AllowClose();
                _mainWindow.Close();
                _logger.Dispose();
                exitCompleted = true;
                Shutdown(0);
            });
        } finally {
            if (!exitCompleted) {
                _environment.Closing = false;
                _mainWindow?.SetExitInProgress(false);
            }
        }
    }

    private static bool ConfirmExit() => WpfMessageBox.Show(
        "桌面分身仍在运行。退出程序将先停止正在运行的任务，再注销该 Session，并结束其中的游戏和 MaaNOP。\n\n确认退出吗？",
        "确认退出",
        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    // The tray and the main window end the Runtime Environment through this routine. The question is asked inside
    // the operation gate, so an exit that started meanwhile turns it into a decline.
    internal async Task EndEnvironmentAsync()
    {
        if (IsExiting || _environment is null) {
            return;
        }

        var ended = await _environment.EndAsync(() => !IsExiting && WpfMessageBox.Show(
            "结束桌面分身将先停止正在运行的任务，再注销 Session，并结束其中运行的程序。确认继续吗？",
            "结束桌面分身",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        if (ended.Error is not { } error) {
            return;
        }
        ShowMainWindow();
        WpfMessageBox.Show(
            $"{error.Message}已保留 Worker Admission，可再次结束桌面分身；详细信息已写入日志。",
            "结束桌面分身未确认",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private bool IsExiting => _environment?.Closing == true;

    private void CreateTrayIcon()
    {
        if (_mainWindow is null || _sessionManager is null || _logger is null) {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("显示子桌面", null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await ShowChildDesktopFromTrayAsync()).Task.Unwrap());
        menu.Items.Add("结束桌面分身", null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await EndEnvironmentAsync()).Task.Unwrap());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出程序", null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await RequestExitAsync()).Task.Unwrap());
        menu.ItemClicked += (_, args) => _logger.Info($"托盘操作{AppLogger.Field("action", args.ClickedItem?.Text)}");

        _trayIcon = new Forms.NotifyIcon {
            Icon = LoadTrayIcon(),
            Text = "NarutoAutoGUI",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void WorkerCoordinator_StateChanged(object? sender, WorkerCoordinatorSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => {
            var notification = _terminalNotifications.Observe(snapshot);
            if (notification is null || IsExiting || _trayIcon is null
                || _mainWindow is { IsVisible: true, IsActive: true, WindowState: not WindowState.Minimized }) {
                return;
            }
            try {
                _trayIcon.ShowBalloonTip(5000, notification.Title, notification.Message,
                    notification.Failed ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
            } catch (System.ComponentModel.Win32Exception exception) {
                _logger?.Warn("显示任务通知失败。", exception);
            }
        });
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is not null) {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream, 32, 32);
            }
        } catch {
            // Fall back to exe icon or system default if resource stream is unavailable.
        }

        try {
            if (!string.IsNullOrEmpty(Environment.ProcessPath) &&
                !Path.GetFileNameWithoutExtension(Environment.ProcessPath)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (extracted is not null) {
                    return extracted;
                }
            }
        } catch {
            // Fall back to system icon if extraction fails.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void MainWindow_ProjectIdentityChanged(object? sender, ProjectIdentity identity)
    {
        if (_trayIcon is null) {
            return;
        }
        var previous = _trayIcon.Icon;
        _trayIcon.Icon = (identity.IconPath is { } path ? LoadProjectTrayIcon(path) : null) ?? LoadTrayIcon();
        _trayIcon.Text = identity.Name.Length > 127 ? identity.Name[..127] : identity.Name;
        if (previous != System.Drawing.SystemIcons.Application) {
            previous?.Dispose();
        }
    }

    private System.Drawing.Icon? LoadProjectTrayIcon(string path)
    {
        try {
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)) {
                return new System.Drawing.Icon(path, 32, 32);
            }
            using var source = new System.Drawing.Bitmap(path);
            using var scaled = new System.Drawing.Bitmap(source, 32, 32);
            var handle = scaled.GetHicon();
            try {
                return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
            } finally {
                DestroyIcon(handle);
            }
        } catch (Exception exception) {
            _logger?.Warn($"加载项目托盘图标失败，已使用默认图标。path={path}", exception);
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void MainWindow_HiddenToTray(object? sender, EventArgs e)
    {
        if (_hasShownTrayNotification || _trayIcon is null) {
            return;
        }

        _hasShownTrayNotification = true;
        _trayIcon.BalloonTipTitle = "NarutoAutoGUI 仍在运行";
        _trayIcon.BalloonTipText = "主窗口已隐藏到托盘。双击托盘图标可恢复窗口，右键可退出程序。";
        _trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(5000);
        _logger?.Info("已显示首次关闭到托盘提示。");
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private async Task ShowChildDesktopFromTrayAsync()
    {
        if (IsExiting || _sessionManager is null || _logger is null || _environment is null) {
            return;
        }

        try {
            await _environment.RunExclusiveAsync(() => _sessionManager.EnsureConnectedAsync(showPreview: true));
        } catch (OperationCanceledException) when (IsExiting) {
            return;
        } catch (Exception exception) {
            _logger.Error("从托盘显示子桌面失败。", exception);
            ShowMainWindow();
            WpfMessageBox.Show(
                $"{exception.GetBaseException().Message}\n\n请在主窗口检查桌面分身状态后重试；详细信息已写入日志。",
                "显示子桌面失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RegisterGlobalExceptionHandlers(AppLogger logger)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Critical("GUI 线程发生未处理异常，应用将继续运行。", args.Exception);
            args.Handled = true;
            WpfMessageBox.Show(
                $"操作发生异常，但程序仍在运行：\n{args.Exception.GetBaseException().Message}\n\n请查看日志。",
                "NarutoAutoGUI",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            logger.Critical("进程发生不可恢复的未处理异常。", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Error("后台任务发生未观察异常。", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Sends what is queued within one second.
        _telemetry.Dispose();
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        try {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var mutexName = $@"Local\NarutoAutoGUI-{currentProcess.SessionId}";
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (!createdNew) {
                mutex.Dispose();
                Environment.ExitCode = 0;
                WpfMessageBox.Show(
                    "NarutoAutoGUI 已在当前 Windows Session 中运行。请从任务栏或托盘打开现有实例。",
                    "NarutoAutoGUI",
                MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            _singleInstanceMutex = mutex;
            _ownsSingleInstanceMutex = true;
            return true;
        } catch (Exception exception) {
            Environment.ExitCode = 1;
            WpfMessageBox.Show(
                $"无法建立 NarutoAutoGUI 单实例保护，程序将退出：\n\n{exception.GetBaseException().Message}",
                "NarutoAutoGUI",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex is null) {
            return;
        }

        if (_ownsSingleInstanceMutex) {
            try {
                _singleInstanceMutex.ReleaseMutex();
            } catch (ApplicationException) {
                // The process is already exiting; disposing the handle is sufficient fallback.
            }
        }

        _ownsSingleInstanceMutex = false;
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }
}
