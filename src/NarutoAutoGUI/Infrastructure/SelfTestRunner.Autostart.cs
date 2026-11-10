using System.Xml.Linq;
using NarutoAutoGUI.Settings;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private const string AutostartExecutable = @"C:\Program Files\Naruto Auto\NarutoAutoGUI.exe";
    private const string AutostartUser = @"PC\小号";

    // For tests that build settings or a window without caring about logon start: never touches Task Scheduler.
    private static AutostartTask InertAutostart() => new(AutostartExecutable, AutostartUser, new FakeSchtasks().Run);

    // Stands in for schtasks.exe: remembers what was asked and answers like a task store would.
    private sealed class FakeSchtasks
    {
        internal List<string[]> Calls { get; } = [];
        internal bool Registered { get; set; }
        internal int CreateExitCode { get; set; }
        internal string? TaskXml { get; private set; }
        internal string? TaskXmlPath { get; private set; }

        internal int Run(IReadOnlyList<string> arguments)
        {
            Calls.Add([.. arguments]);
            switch (arguments[0]) {
                case "/create":
                    // The definition file only lives for the call, so it is read now.
                    TaskXmlPath = arguments[Array.IndexOf(arguments.ToArray(), "/xml") + 1];
                    TaskXml = File.ReadAllText(TaskXmlPath);
                    Registered |= CreateExitCode == 0;
                    return CreateExitCode;
                case "/delete":
                    Registered = false;
                    return 0;
                default:
                    return Registered ? 0 : 1;
            }
        }
    }

    private static void VerifyAutostartTask()
    {
        var store = new FakeSchtasks();
        var task = new AutostartTask(AutostartExecutable, AutostartUser, store.Run);
        Require(!task.IsEnabled(), "没有注册计划任务时开机自启必须是关闭。");
        task.Disable();
        Require(store.Calls.All(call => call[0] == "/query"), "关闭一个不存在的自启任务不能报错，也不必删除。");

        task.Enable();
        var create = store.Calls.Single(call => call[0] == "/create");
        string Argument(string name) => create[Array.IndexOf(create, name) + 1];
        Require(create.Contains("/f") && task.IsEnabled(), "重复启用须覆盖旧任务，启用后查询为开启。");
        Require(!File.Exists(store.TaskXmlPath), "任务定义是临时文件，用完必须删除。");
        // schtasks flags cannot express the battery and time-limit settings, so the task is defined as XML.
        var definition = XDocument.Parse(store.TaskXml!);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(params string[] path) => path.Aggregate((XContainer)definition, (node, name) =>
            node.Descendants(ns + name).First()) is XElement element ? element.Value : "";
        Require(Value("LogonTrigger", "UserId") == AutostartUser && Value("Principal", "UserId") == AutostartUser
            && Value("Principal", "RunLevel") == "HighestAvailable"
            && Value("Principal", "LogonType") == "InteractiveToken",
            "必须只在该用户登录时、以最高权限在交互桌面启动，才不会每次登录弹 UAC。");
        Require(Value("LogonTrigger", "Delay") == "PT30S", "必须延迟 30 秒，等桌面会话就绪。");
        Require(Value("Exec", "Command") == AutostartExecutable && Value("Exec", "Arguments") == "--autostart",
            "启动命令必须是本程序并带 --autostart。");
        Require(Value("Settings", "DisallowStartIfOnBatteries") == "false"
            && Value("Settings", "StopIfGoingOnBatteries") == "false",
            "笔记本使用电池登录时也必须启动，且不能因切到电池而被停掉。");
        Require(Value("Settings", "ExecutionTimeLimit") == "PT0S", "不能设置运行时限，否则程序会在默认的 72 小时后被结束。");

        task.Disable();
        Require(store.Calls.Last().SequenceEqual(["/delete", "/tn", Argument("/tn"), "/f"]) && !task.IsEnabled(),
            "关闭必须删除计划任务。");

        store.CreateExitCode = 1;
        Require(Throws<InvalidOperationException>(task.Enable) && !task.IsEnabled(), "创建失败必须报错，且不得显示为已开启。");
    }

    private static void VerifyStartupSettings(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "startup-settings");
        var store = new FakeSchtasks();
        ApplicationSettings Create() => new(directory, logger, () => Task.CompletedTask, () => Task.CompletedTask,
            () => { }, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => { },
            _ => { }, _ => { }, new AutostartTask(AutostartExecutable, AutostartUser, store.Run));
        var daily = new AutoRunTarget(Guid.NewGuid(), "日常");
        var weekly = new AutoRunTarget(Guid.NewGuid(), "周常");

        var settings = Create();
        Require(!settings.Autostart.Value && store.Calls.All(call => call[0] == "/query"),
            "开机自启的初始状态必须来自系统，读取不得创建任务。");
        settings.Autostart.Value = true;
        Require(store.Registered && Create().Autostart.Value, "开启必须创建计划任务，重启后仍显示开启。");
        settings.Autostart.Value = false;
        Require(!store.Registered && !Create().Autostart.Value, "关闭必须删除计划任务。");
        store.CreateExitCode = 1;
        settings.Autostart.Value = true;
        Require(!settings.Autostart.Value && !store.Registered && settings.Autostart.Status != "",
            "创建任务失败必须恢复为关闭并提示。");

        var choice = settings.AutoRunConfiguration;
        Require(choice.Options.Count == 1 && choice.Selected.Id is null && !settings.AutoRunOnManualLaunch.IsEnabled,
            "尚未读到配置时只有“不自动执行”，手动启动开关不可用。");
        settings.UpdateAutoRunConfigurations([daily, weekly]);
        Require(choice.Options.Select(option => option.Name).SequenceEqual(["不自动执行", "日常", "周常"]),
            "选项必须是“不自动执行”加全部配置。");
        choice.Selected = choice.Options[2];
        settings.AutoRunOnManualLaunch.Value = true;
        Require(settings.AutoRunConfigurationId == weekly.Id && settings.AutoRunOnManualLaunch.IsEnabled,
            "选择配置后手动启动开关可用。");

        var restarted = Create();
        restarted.UpdateAutoRunConfigurations([daily, weekly with { Name = "周常（改名）" }]);
        Require(restarted.AutoRunConfigurationId == weekly.Id && restarted.AutoRunOnManualLaunch.Value
            && restarted.AutoRunConfiguration.Selected.Name == "周常（改名）", "重启后必须恢复选择与开关，改名不影响选择。");
        restarted.AutoRunConfiguration.Selected = restarted.AutoRunConfiguration.Options[0];
        Require(restarted.AutoRunConfigurationId is null && !restarted.AutoRunOnManualLaunch.IsEnabled
            && Create().AutoRunConfigurationId is null, "选择“不自动执行”后不再有目标，手动启动开关不可用。");

        restarted.AutoRunConfiguration.Selected = restarted.AutoRunConfiguration.Options[1];
        restarted.AutoRunOnManualLaunch.Value = true;
        var afterDelete = Create();
        afterDelete.UpdateAutoRunConfigurations([weekly]);
        Require(afterDelete.AutoRunConfigurationId is null && !afterDelete.AutoRunOnManualLaunch.Value,
            "所选配置被删除后必须静默回退为“不自动执行”并关闭手动启动开关。");
        var later = Create();
        later.UpdateAutoRunConfigurations([daily, weekly]);
        Require(later.AutoRunConfigurationId is null && !later.AutoRunOnManualLaunch.Value,
            "回退必须持久化，配置再次出现也不能自动恢复。");

        var path = Path.Combine(directory, "config", "autorun-configuration.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)) {
            later.AutoRunConfiguration.Selected = later.AutoRunConfiguration.Options[1];
            Require(later.AutoRunConfigurationId is null && later.AutoRunConfiguration.Status != "",
                "保存失败必须恢复原选择并提示。");
        }

        var page = settings.Page.Sections.SelectMany(section => section.Items).ToArray();
        Require(page.Single(item => item.Definition.ChoiceKey == "autorun.configuration").Choice == choice
            && page.Any(item => item.Toggle == settings.Autostart)
            && page.Any(item => item.Toggle == settings.AutoRunOnManualLaunch), "三项启动设置必须出现在设置页并绑定。");
        const string choiceWithoutKey = """
            {"sections":[{"id":"s","title":"S","icon":"Options20","items":[
              {"id":"c","type":"choice","icon":"Play20","title":"C"}]}]}
            """;
        Require(Throws<InvalidDataException>(() => SettingsDefinition.Parse(choiceWithoutKey)),
            "选择行必须声明 choiceKey。");
    }
}
