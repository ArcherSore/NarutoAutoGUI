using System.Windows;
using System.Windows.Threading;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Views;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace NarutoAutoGUI;

public partial class App : System.Windows.Application
{
    private AppLogger? _logger;
    private ChildSessionManager? _sessionManager;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = SelfTestRunner.Run();
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
        var settings = settingsStore.Load();
        _sessionManager = new ChildSessionManager(_logger);
        var programService = new ChildSessionProgramService(_logger);
        _mainWindow = new MainWindow(
            _logger,
            settingsStore,
            settings,
            _sessionManager,
            programService,
            RequestExitAsync);
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

        bool hasChildSession;
        try
        {
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

        _isExiting = true;
        _logger.Info("NarutoAutoGUI 正常退出。");
        _trayIcon?.Dispose();
        _trayIcon = null;
        _sessionManager.Dispose();
        _mainWindow.AllowClose();
        _mainWindow.Close();
        _logger.Dispose();
        Shutdown(0);
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
        if (_sessionManager is null || _logger is null)
        {
            return;
        }

        try
        {
            await _sessionManager.EnsureConnectedAsync(showPreview: true);
        }
        catch (Exception exception)
        {
            _logger.Error("从托盘显示子桌面失败。", exception);
            ShowMainWindow();
            WpfMessageBox.Show(
                exception.GetBaseException().Message,
                "显示子桌面失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task TerminateFromTrayAsync()
    {
        if (_sessionManager is null || _logger is null)
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
            await _sessionManager.TerminateAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("从托盘结束桌面分身失败。", exception);
            ShowMainWindow();
            WpfMessageBox.Show(
                exception.GetBaseException().Message,
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
}
