using System.Windows;
using System.Windows.Threading;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace NarutoAutoGUI;

public partial class App : System.Windows.Application
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private AppLogger? _logger;
    private ChildSessionManager? _sessionManager;
    private WorkerCoordinator? _workerCoordinator;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _isExiting;
    private bool _hasShownTrayNotification;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = SelfTestRunner.Run();
            Shutdown(Environment.ExitCode);
            return;
        }

        if (!TryAcquireSingleInstance())
        {
            Shutdown(Environment.ExitCode);
            return;
        }

        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        _logger = new AppLogger();
        RegisterGlobalExceptionHandlers(_logger);
        _logger.Info("NarutoAutoGUI 正式 GUI 启动。");
        _logger.Debug($"Process={Environment.ProcessPath}；OS={Environment.OSVersion}；"
                      + $"64BitOS={Environment.Is64BitOperatingSystem}；64BitProcess={Environment.Is64BitProcess}。");

        var settingsStore = new AppSettingsStore(_logger);
        AppSettings settings;
        try
        {
            settings = settingsStore.Load();
        }
        catch (Exception exception)
        {
            _logger.Error("Application Settings 无效，正常启动已阻止。", exception);
            var answer = WpfMessageBox.Show(
                $"Application Settings 无法读取，原文件尚未修改：\n\n{exception.GetBaseException().Message}"
                + "\n\n是否明确重置为默认 Application Settings 后继续？",
                "配置需要处理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                Shutdown(1);
                return;
            }

            settings = new AppSettings();
            try
            {
                settingsStore.Save(settings);
                _logger.Warn("用户已明确重置 Application Settings。 ");
            }
            catch (Exception saveException)
            {
                _logger.Critical("重置 Application Settings 失败。", saveException);
                WpfMessageBox.Show(
                    $"重置 Application Settings 失败：\n\n{saveException.GetBaseException().Message}",
                    "NarutoAutoGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }
        _sessionManager = new ChildSessionManager(_logger);
        _workerCoordinator = new WorkerCoordinator(
            _logger,
            Path.Combine(AppContext.BaseDirectory, "state"),
            Path.Combine(AppContext.BaseDirectory, "worker", "NarutoAutoWorker.exe"));
        var programService = new ChildSessionProgramService(_logger);
        _mainWindow = new MainWindow(
            _logger,
            settingsStore,
            settings,
            _sessionManager,
            programService,
            _workerCoordinator,
            RunApplicationOperationAsync,
            RequestExitAsync);
        _mainWindow.HiddenToTray += MainWindow_HiddenToTray;
        MainWindow = _mainWindow;
        CreateTrayIcon();
        _mainWindow.Show();
    }

    internal async Task RequestExitAsync()
    {
        if (_isExiting || _logger is null || _sessionManager is null || _mainWindow is null)
        {
            return;
        }

        _isExiting = true;
        _mainWindow.SetExitInProgress(true);
        var gateEntered = false;
        var exitCompleted = false;
        try
        {
            await _operationGate.WaitAsync();
            gateEntered = true;

            bool hasChildSession;
            try
            {
                // The application operation gate is held here, so this query observes the final
                // state after any in-flight create/connect/launch operation has completed.
                hasChildSession = _sessionManager.HasChildSession;
            }
            catch (Exception exception)
            {
                _logger.Error("退出前检查 Child Session 失败。", exception);
                WpfMessageBox.Show(
                    "无法确认桌面分身状态，已取消退出。请查看日志后重试。",
                    "NarutoAutoGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (hasChildSession)
            {
                var answer = WpfMessageBox.Show(
                    "桌面分身仍在运行。退出程序将注销该 Session，并结束其中的游戏和 MaaNOP。\n\n确认退出吗？",
                    "确认退出",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }

                try
                {
                    await _sessionManager.TerminateAsync();
                    _workerCoordinator?.ChildSessionEnded();
                }
                catch (Exception exception)
                {
                    _logger.Critical("退出前注销 Child Session 失败，已取消退出。", exception);
                    WpfMessageBox.Show(
                        "注销桌面分身失败，已取消退出，避免遗留未知状态。请查看日志。",
                        "NarutoAutoGUI",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }

            try
            {
                if (_sessionManager.HasChildSession)
                {
                    _logger.Critical("最终退出检查仍检测到 Child Session，已取消退出。");
                    WpfMessageBox.Show(
                        "最终检查仍检测到桌面分身，已取消退出，避免遗留未知状态。请查看日志后重试。",
                        "NarutoAutoGUI",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }
            catch (Exception exception)
            {
                _logger.Critical("最终退出检查 Child Session 失败，已取消退出。", exception);
                WpfMessageBox.Show(
                    "最终检查无法确认桌面分身状态，已取消退出。请查看日志后重试。",
                    "NarutoAutoGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _logger.Info("NarutoAutoGUI 正常退出。");
            _trayIcon?.Dispose();
            _trayIcon = null;
            _sessionManager.Dispose();
            if (_workerCoordinator is not null)
            {
                await _workerCoordinator.DisposeAsync();
                _workerCoordinator = null;
            }
            _mainWindow.HiddenToTray -= MainWindow_HiddenToTray;
            _mainWindow.AllowClose();
            _mainWindow.Close();
            _logger.Dispose();
            exitCompleted = true;
            Shutdown(0);
        }
        finally
        {
            if (gateEntered)
            {
                _operationGate.Release();
            }

            if (!exitCompleted)
            {
                _isExiting = false;
                _mainWindow?.SetExitInProgress(false);
            }
        }
    }

    private void CreateTrayIcon()
    {
        if (_mainWindow is null || _sessionManager is null || _logger is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("显示子桌面", null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await ShowChildDesktopFromTrayAsync()).Task.Unwrap());
        menu.Items.Add("结束桌面分身", null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await TerminateFromTrayAsync()).Task.Unwrap());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出程序", null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () => await RequestExitAsync()).Task.Unwrap());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "NarutoAutoGUI",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void MainWindow_HiddenToTray(object? sender, EventArgs e)
    {
        if (_hasShownTrayNotification || _trayIcon is null)
        {
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
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private async Task ShowChildDesktopFromTrayAsync()
    {
        if (_isExiting || _sessionManager is null || _logger is null)
        {
            return;
        }

        try
        {
            await RunApplicationOperationAsync(() => _sessionManager.EnsureConnectedAsync(showPreview: true));
        }
        catch (OperationCanceledException) when (_isExiting)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.Error("从托盘显示子桌面失败。", exception);
            ShowMainWindow();
            WpfMessageBox.Show(
                $"{exception.GetBaseException().Message}\n\n请在主窗口检查桌面分身状态后重试；详细信息已写入日志。",
                "显示子桌面失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task TerminateFromTrayAsync()
    {
        if (_isExiting || _sessionManager is null || _logger is null)
        {
            return;
        }

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

        try
        {
            await RunApplicationOperationAsync(() => _sessionManager.TerminateAsync());
            _workerCoordinator?.ChildSessionEnded();
        }
        catch (OperationCanceledException) when (_isExiting)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.Error("从托盘结束桌面分身失败。", exception);
            ShowMainWindow();
            WpfMessageBox.Show(
                $"{exception.GetBaseException().Message}\n\n请在主窗口检查桌面分身状态后重试；详细信息已写入日志。",
                "结束桌面分身失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private async Task RunApplicationOperationAsync(Func<Task> operation)
    {
        if (_isExiting)
        {
            throw new OperationCanceledException("应用正在退出，未开始新的操作。");
        }

        await _operationGate.WaitAsync();
        try
        {
            if (_isExiting)
            {
                throw new OperationCanceledException("应用正在退出，未开始新的操作。");
            }

            await operation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var mutexName = $@"Local\NarutoAutoGUI-{currentProcess.SessionId}";
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                Environment.ExitCode = 0;
                WpfMessageBox.Show(
                    "NarutoAutoGUI 已在当前 Windows Session 中运行。请从任务栏或托盘打开现有实例。",
                    "NarutoAutoGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            _singleInstanceMutex = mutex;
            _ownsSingleInstanceMutex = true;
            return true;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            WpfMessageBox.Show(
                $"无法建立 NarutoAutoGUI 单实例保护，程序将退出：\n\n{exception.GetBaseException().Message}",
                "NarutoAutoGUI",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex is null)
        {
            return;
        }

        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already exiting; disposing the handle is sufficient fallback.
            }
        }

        _ownsSingleInstanceMutex = false;
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }
}
