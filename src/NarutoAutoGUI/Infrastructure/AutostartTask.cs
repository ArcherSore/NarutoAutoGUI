using System.Diagnostics;

namespace NarutoAutoGUI.Infrastructure;

// Start at logon through Task Scheduler. The program requires administrator rights, so a Run key would be blocked by
// UAC; a task with the highest run level starts elevated without a prompt. The task is the only record of the choice.
internal sealed class AutostartTask(string executablePath, Func<IReadOnlyList<string>, int> schtasks)
{
    private const string TaskName = "NarutoAutoGUI";

    internal static AutostartTask ForCurrentProcess() => new(Environment.ProcessPath ?? "NarutoAutoGUI.exe", Run);

    internal bool IsEnabled() => schtasks(["/query", "/tn", TaskName]) == 0;

    internal void Enable()
    {
        // The delay lets the desktop session finish starting before the Child Session is created.
        if (schtasks(["/create", "/tn", TaskName, "/tr", $"\"{executablePath}\" --autostart", "/sc", "onlogon",
                "/delay", "0000:30", "/it", "/rl", "highest", "/f"]) != 0) {
            throw new InvalidOperationException("创建开机自启动计划任务失败。");
        }
    }

    internal void Disable()
    {
        if (IsEnabled() && schtasks(["/delete", "/tn", TaskName, "/f"]) != 0) {
            throw new InvalidOperationException("删除开机自启动计划任务失败。");
        }
    }

    private static int Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe") {
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
