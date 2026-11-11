using System.Diagnostics;
using System.Text;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyLaunchHelp()
    {
        Require(LaunchCommand.Parse(["--help"]).Help && LaunchCommand.Parse(["-H"]).Help
            && !LaunchCommand.Parse(["--autostart"]).Help, "-h 和 --help 都请求帮助，其他参数不请求。");
        foreach (var option in new[] { "--autostart", "-i", "--instance", "-q", "--quit-after-run", "-h", "--help" }) {
            Require(LaunchCommand.HelpText.Contains(option, StringComparison.Ordinal), $"帮助必须说明 {option}。");
        }
    }

    // The real executable, started the way a terminal does: nothing elevates before it can print, so no UAC prompt
    // can stand in front of --help. Also proves the executable starts at all without administrator rights.
    private static void VerifyHelpOutput()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "NarutoAutoGUI.exe");
        if (!File.Exists(executable)) {
            throw new SelfTestSkip("找不到 NarutoAutoGUI.exe，无法启动真实程序。");
        }
        using var process = Process.Start(new ProcessStartInfo(executable, "--help") {
            RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true
        })!;
        // Reading to the end would wait as long as the process runs, so the limit is applied to the exit instead.
        var reading = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(15000)) {
            process.Kill();
            throw new InvalidOperationException("--help 必须输出后立即退出。");
        }
        var output = reading.Result;
        Require(process.ExitCode == 0 && output.Contains("--quit-after-run", StringComparison.Ordinal),
            $"--help 必须在 stdout 输出帮助并以 0 退出：exit={process.ExitCode}，输出={output}");
    }
}
