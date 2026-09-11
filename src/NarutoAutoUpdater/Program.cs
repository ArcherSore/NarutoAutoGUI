using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NarutoAutoGUI.Updates;

namespace NarutoAutoUpdater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--probe" })) {
            return 0;
        }
        if (args.Length != 1) {
            return 1;
        }
        var application = new Application();
        var status = new TextBlock { Text = "正在准备更新…", Margin = new Thickness(0, 16, 0, 16) };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "正在更新 MaaNOP", FontSize = 20 });
        panel.Children.Add(status);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
        var window = new Window {
            Title = "MaaNOP 更新", Width = 390, Height = 185, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel
        };
        var finished = false;
        var exitCode = 0;
        window.Closing += (_, e) => e.Cancel = !finished;
        window.Loaded += async (_, _) =>
        {
            try {
                var handoff = JsonSerializer.Deserialize<UpdateHandoff>(File.ReadAllText(args[0]))
                    ?? throw new InvalidDataException("更新交接信息无效。");
                var ownDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
                var installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoff.Update.Installation));
                if (ownDirectory.Equals(installation, StringComparison.OrdinalIgnoreCase)
                    || ownDirectory.StartsWith(installation + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("Updater 必须从安装目录外运行。");
                }
                var logPath = Path.Combine(ownDirectory, "updater.log");
                void Log(string message)
                {
                    try {
                        File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                        // Logging failure must not turn a completed directory move into a failed transaction.
                        Debug.WriteLine(exception);
                    }
                }
                await Task.Run(async () =>
                {
                    using var lease = UpdateStorage.AcquireInstallLock(installation);
                    Log("WaitingForProcesses：等待 GUI 退出。");
                    try {
                        using var gui = Process.GetProcessById(handoff.GuiPid);
                        await gui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    } catch (ArgumentException) {
                        // The tracked GUI already exited before the temporary updater started.
                    }
                    window.Dispatcher.Invoke(() => status.Text = "正在安装更新…");
                    new InstallTransaction(Log).Install(installation, handoff.Update.Staging, handoff.Update.Tag);
                    try {
                        UpdateStorage.SaveCompletion(handoff.Update);
                    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                        Log($"保存完成提示失败，仍启动新版：{exception.Message}");
                    }
                    window.Dispatcher.Invoke(() => status.Text = "正在启动 NarutoAutoGUI…");
                    // Release before startup: the GUI uses the same lock to reject launches during a swap.
                    lease.Dispose();
                    try {
                        using var process = Process.Start(new ProcessStartInfo {
                            FileName = Path.Combine(installation, "NarutoAutoGUI.exe"),
                            WorkingDirectory = installation, UseShellExecute = true
                        }) ?? throw new IOException("无法启动新版 NarutoAutoGUI。");
                        Log($"新版本启动成功，PID={process.Id}。");
                    } catch (Exception exception) {
                        Log($"新版本启动失败：{exception}");
                        throw;
                    }
                });
            } catch (Exception exception) {
                exitCode = 1;
                try {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "updater.log"), exception.ToString());
                } catch (IOException) { }
                MessageBox.Show(window, "更新未完成，请保留旧备份并查看更新日志。\n" + exception.Message,
                    "MaaNOP 更新", MessageBoxButton.OK, MessageBoxImage.Error);
            } finally {
                finished = true;
                window.Close();
            }
        };
        application.Run(window);
        return exitCode;
    }
}
