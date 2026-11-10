using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    // Windows' own parser is the independent source of truth for what the elevated process will receive.
    private static string[] SplitLikeWindows(string arguments)
    {
        var argv = CommandLineToArgvW("program " + arguments, out var count);
        try {
            return Enumerable.Range(1, count - 1)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!)
                .ToArray();
        } finally {
            LocalFree(argv);
        }
    }

    private static void VerifyForwardedArguments()
    {
        string[][] cases = [
            [], ["--autostart", "-i", "日常 任务", "-q"], ["-i", "say \"hi\""], ["-i", @"C:\Program Files\"],
            ["-i", @"back\slash\"], ["-i", ""], ["--instance=a b", "tab\there"], ["-i", @"end\"" quote"]
        ];
        foreach (var arguments in cases) {
            var joined = LaunchCommand.JoinArguments(arguments);
            Require(SplitLikeWindows(joined).SequenceEqual(arguments),
                $"提权重启必须把参数原样交给新进程：{joined}");
        }
    }

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
        var output = process.StandardOutput.ReadToEnd();
        Require(process.WaitForExit(15000), "--help 必须输出后立即退出。");
        Require(process.ExitCode == 0 && output.Contains("--quit-after-run", StringComparison.Ordinal),
            $"--help 必须在 stdout 输出帮助并以 0 退出：exit={process.ExitCode}，输出={output}");
    }
}
