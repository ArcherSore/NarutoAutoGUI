using System.Windows.Forms;

namespace MaaNOP.ChildSessionLauncher;

// Entry point:
//   - No args            -> connect, then launch MaaNOP in the Child Session.
//   - --exec <exe path>  -> connect, then launch only the given .exe (generic PoC test entry).
//
// Flow: enable Child Session -> connect RDP (1920x1080) -> launch target -> verify ->
// (keep preview window open) -> on close: disconnect -> logoff Child Session -> exit.
internal static class Program
{
    private const string DefaultMaaNopPath =
        @"D:\Automation Script\MaaNOP-win-x86_64-v1.3.0\MFAAvalonia.exe";

    private static readonly string LogFile =
        Path.Combine(Path.GetTempPath(), "MaaNOP.ChildSessionLauncher.log");

    [STAThread]
    private static void Main(string[] args)
    {
        // Global crash diagnostics: write to console + a log file + pop a MessageBox, so a silent
        // crash (e.g. ActiveX handle creation failing before Shown) is never invisible again.
        AppDomain.CurrentDomain.UnhandledException += (_, ue) =>
            ReportCrash("UnhandledException", ue.ExceptionObject as Exception);
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }
        catch
        {
            // best effort
        }
        Application.ThreadException += (_, te) => ReportCrash("ThreadException", te.Exception);

        try
        {
            RunMain(args);
        }
        catch (Exception ex)
        {
            ReportCrash("Main", ex);
        }

        Console.WriteLine("退出。按任意键关闭控制台...");
        try { Console.ReadKey(true); } catch { }
    }

    private static void RunMain(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var (execPath, userName, password) = ParseArguments(args);

        Log("MaaNOP Child Session Launcher PoC"
            + (execPath is null ? " (MaaNOP；游戏手动启动)" : " (--exec 单目标测试)")
            + (password is null ? "" : " (--password 自动登录)"));
        Log("-------------------------------------------");
        Log($"日志文件: {LogFile}");
        Log($"进程: {Environment.ProcessPath}");
        Log($"OS: {Environment.OSVersion}  64bit={Environment.Is64BitOperatingSystem}  主机64bit={Environment.Is64BitProcess}");

        using var form = new RdpPreviewForm();
        var service = new ChildSessionService(form);

        form.Shown += async (s, e) =>
        {
            try
            {
                Log("检测 Child Session 支持（运行时探测，不按 Windows 版本预判）...");
                Log($"  ChildSessionsEnabled = {ChildSessionService.IsChildSessionsEnabled()}");
                Log($"  RDP 端口             = {ChildSessionService.GetRdpPort()}");
                Log($"  RDP Wrapper 检测     = {ChildSessionService.IsRdpWrapPresent()}（仅信息，不依赖）");

                Log("启用 Child Session（需要管理员权限）...");
                ChildSessionService.EnsureChildSessionsEnabled();
                Log($"  ChildSessionsEnabled = {ChildSessionService.IsChildSessionsEnabled()}");

                Log("正在连接 RDP 到 Child Session（固定 1920x1080 @ 100%，预览启用 SmartSizing）...");
                await service.ConnectAsync(CancellationToken.None, userName, password);

                var childSessionId = service.ChildSessionId
                    ?? throw new InvalidOperationException("连接后未取得 Child Session ID。");
                Log($"已连接。 childSessionId = {childSessionId}  ConnectedState = {service.ConnectedState}");
                form.Text = $"MaaNOP Child Session #{childSessionId} (1920x1080 @ 100%) — 关闭窗口以清理";

                var targets = execPath is null
                    ? new[] { new LaunchTarget(DefaultMaaNopPath, "MFAAvalonia.exe") }
                    : new[] { new LaunchTarget(execPath, Path.GetFileName(execPath)) };

                // Each target is isolated: one launch failure must not prevent the other target
                // from starting or tear down the established Child Session / preview window.
                foreach (var target in targets)
                {
                    try
                    {
                        Log($"在 Child Session {childSessionId} 中启动: {target.ExecutablePath}");
                        await ChildSessionProcessLauncher.LaunchAsync(childSessionId, target.ExecutablePath);
                        Log("  启动请求已通过任务计划程序 COM RunEx 提交。");

                        await VerifyInChildSessionAsync(target.VerificationProcessName, childSessionId);
                    }
                    catch (Exception ex)
                    {
                        Log($"[WARN] 启动/验证 {target.ExecutablePath} 时出错（继续处理其他目标）: {ex.GetBaseException().Message}");
                    }
                }

                Log("主桌面仍可正常使用。关闭本预览窗口将断开并注销 Child Session。");
            }
            catch (Exception ex)
            {
                var msg = ex.ToString();
                Log($"[ERROR] {msg}");
                form.Text = "MaaNOP Child Session — 启动失败（详见控制台/日志）";
                form.BeginInvoke(new Action(() => form.Close()));
            }
        };

        form.FormClosing += (s, e) =>
        {
            try
            {
                service.Disconnect();
                Log("已断开 RDP 连接。");
            }
            catch (Exception ex)
            {
                Log($"断开 RDP 时忽略异常：{ex.GetBaseException().Message}");
            }
        };

        Log("即将进入消息循环 (Application.Run)，创建 RDP ActiveX 控件句柄...");
        try
        {
            Application.Run(form);
        }
        catch (Exception ex)
        {
            // AxHost handle creation / ActiveX instantiation failures surface here, before Shown.
            Log($"[Application.Run 异常] {ex}");
            MessageBox.Show(
                $"Application.Run 抛出异常（通常是 MsRdpClient10 ActiveX 未能创建/注册）：\n\n{ex.GetBaseException().Message}\n\n完整日志：{LogFile}",
                "MaaNOP Child Session Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        // After the preview window is closed: logoff the Child Session.
        try
        {
            var id = ChildSessionService.TerminateChildSession(wait: true);
            Log(id is null
                ? "当前无 Child Session 需要注销。"
                : $"已注销 Child Session {id.Value}。");
        }
        catch (Exception ex)
        {
            Log($"注销 Child Session 时出错：{ex.GetBaseException().Message}");
        }
    }

    // Poll WMI Win32_Process to confirm the launched process actually runs inside the
    // Child Session (not the main session). Visual confirmation is the RDP preview window.
    private static async Task VerifyInChildSessionAsync(string processName, uint childSessionId)
    {
        Log($"验证 {processName} 是否运行在 Child Session {childSessionId} ...");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (ChildSessionNativeMethods.TryFindProcessInSession(processName, childSessionId, out var pid))
            {
                Log($"  [OK] {processName}  PID={pid}  SessionId={childSessionId} (== childSessionId)");
                return;
            }

            await Task.Delay(500);
        }

        Log($"  [WARN] 10 秒内未在 Child Session {childSessionId} 中枚举到 {processName}。");
        Log("         （可能权限不足或进程名不同；请在 RDP 预览窗口中目视确认。）");
    }

    // Supports: --exec <path> | --exec=<path>, --user <name>, --password <pw> (or env
    // MAANOP_RDP_PASSWORD). Only these switches for the first version. The password is never
    // stored in code; it is supplied at runtime for the programmatic RDP logon (put_ClearTextPassword).
    private static (string? ExecPath, string? UserName, string? Password) ParseArguments(string[] args)
    {
        string? execPath = null;
        string? userName = null;
        string? password = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;

            if (arg.StartsWith("--exec=", StringComparison.Ordinal))
            {
                execPath = arg.Substring("--exec=".Length);
            }
            else if (string.Equals(arg, "--exec", StringComparison.Ordinal))
            {
                if (next is not null) { execPath = next; i++; }
                else Log("[WARN] --exec 之后未提供路径。");
            }
            else if (arg.StartsWith("--user=", StringComparison.Ordinal))
            {
                userName = arg.Substring("--user=".Length);
            }
            else if (string.Equals(arg, "--user", StringComparison.Ordinal))
            {
                if (next is not null) { userName = next; i++; }
                else Log("[WARN] --user 之后未提供值。");
            }
            else if (arg.StartsWith("--password=", StringComparison.Ordinal))
            {
                password = arg.Substring("--password=".Length);
            }
            else if (string.Equals(arg, "--password", StringComparison.Ordinal))
            {
                if (next is not null) { password = next; i++; }
                else Log("[WARN] --password 之后未提供值。");
            }
        }

        if (password is null && Environment.GetEnvironmentVariable("MAANOP_RDP_PASSWORD") is { } envPw)
        {
            password = envPw;
        }

        userName ??= Environment.UserName;

        return (execPath, userName, password);
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
        catch
        {
            // console may be unavailable in some host contexts
        }

        try
        {
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch
        {
            // logging is best-effort
        }
    }

    private static void ReportCrash(string source, Exception? ex)
    {
        Log($"[{source}] {ex}");
        try
        {
            MessageBox.Show(
                $"[{source}]\n{ex?.GetBaseException().Message}\n\n完整日志：{LogFile}",
                "MaaNOP Child Session Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // best effort
        }
    }

    private sealed record LaunchTarget(string ExecutablePath, string VerificationProcessName);
}
