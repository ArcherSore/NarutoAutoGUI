using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using Protocol;

namespace Gui.Infrastructure;

// Start at logon through Task Scheduler. The program requires administrator rights, so a Run key would be blocked by
// UAC; a task with the highest run level starts elevated without a prompt. The task is the only record of the choice.
// It is defined as XML because schtasks flags cannot lift two defaults that would silently stop it: not starting on
// battery, and ending after 72 hours.
internal sealed class AutostartTask(string executablePath, string userName, Func<IReadOnlyList<string>, int> schtasks)
{
    private const string TaskName = AppIdentity.Name;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static AutostartTask ForCurrentProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new(Environment.ProcessPath ?? $"{AppIdentity.Name}.exe", identity.Name, Run);
    }

    internal bool IsEnabled() => schtasks(["/query", "/tn", TaskName]) == 0;

    internal void Enable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{AppIdentity.Name}-autostart-{Guid.NewGuid():N}.xml");
        try {
            Definition().Save(path);
            if (schtasks(["/create", "/tn", TaskName, "/xml", path, "/f"]) != 0) {
                throw new InvalidOperationException("创建开机自启动计划任务失败。");
            }
        } finally {
            File.Delete(path);
        }
    }

    internal void Disable()
    {
        if (IsEnabled() && schtasks(["/delete", "/tn", TaskName, "/f"]) != 0) {
            throw new InvalidOperationException("删除开机自启动计划任务失败。");
        }
    }

    private XDocument Definition()
    {
        XElement Element(string name, params object?[] content) => new(Ns + name, content);
        // The delay lets the desktop session finish starting before the Child Session is created.
        var task = Element("Task", new XAttribute("version", "1.2"),
            Element("Triggers", Element("LogonTrigger",
                Element("Enabled", "true"), Element("UserId", userName), Element("Delay", "PT30S"))),
            Element("Principals", Element("Principal", new XAttribute("id", "Author"), Element("UserId", userName),
                Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
            Element("Settings",
                Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"),
                Element("StopIfGoingOnBatteries", "false"), Element("AllowStartOnDemand", "true"),
                Element("Enabled", "true"), Element("ExecutionTimeLimit", "PT0S")),
            Element("Actions", new XAttribute("Context", "Author"), Element("Exec",
                Element("Command", executablePath), Element("Arguments", "--autostart"),
                Element("WorkingDirectory", Path.GetDirectoryName(executablePath)))));
        return new XDocument(new XDeclaration("1.0", "UTF-16", null), task);
    }

    private static int Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe", arguments) {
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
