using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace NarutoAutoGUI.Infrastructure;

// Start at logon through Task Scheduler. The program requires administrator rights, so a Run key would be blocked by
// UAC; a task with the highest run level starts elevated without a prompt. The task is the only record of the choice.
// It is defined as XML because schtasks flags cannot lift two defaults that would silently stop it: not starting on
// battery, and ending after 72 hours.
internal sealed class AutostartTask(string executablePath, string userName, Func<IReadOnlyList<string>, int> schtasks)
{
    private const string TaskName = "NarutoAutoGUI";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static AutostartTask ForCurrentProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new(Environment.ProcessPath ?? "NarutoAutoGUI.exe", identity.Name, Run);
    }

    internal bool IsEnabled() => schtasks(["/query", "/tn", TaskName]) == 0;

    internal void Enable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"NarutoAutoGUI-autostart-{Guid.NewGuid():N}.xml");
        try {
            File.WriteAllText(path, Definition(), Encoding.Unicode);
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

    private string Definition()
    {
        XElement Element(string name, object? content = null) => new(Ns + name, content);
        // The delay lets the desktop session finish starting before the Child Session is created.
        var task = Element("Task", new object[] {
            new XAttribute("version", "1.2"),
            Element("Triggers", Element("LogonTrigger",
                new[] { Element("Enabled", "true"), Element("UserId", userName), Element("Delay", "PT30S") })),
            Element("Principals", Element("Principal", new object[] {
                new XAttribute("id", "Author"), Element("UserId", userName),
                Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable")
            })),
            Element("Settings", new[] {
                Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"),
                Element("StopIfGoingOnBatteries", "false"), Element("AllowStartOnDemand", "true"),
                Element("Enabled", "true"), Element("ExecutionTimeLimit", "PT0S")
            }),
            Element("Actions", new object[] {
                new XAttribute("Context", "Author"), Element("Exec", new[] {
                    Element("Command", executablePath), Element("Arguments", "--autostart"),
                    Element("WorkingDirectory", Path.GetDirectoryName(executablePath))
                })
            })
        });
        var document = new XDocument(new XDeclaration("1.0", "UTF-16", null), task);
        return $"{document.Declaration}{Environment.NewLine}{document}";
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
