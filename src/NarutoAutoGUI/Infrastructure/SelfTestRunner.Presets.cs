using System.Text.Json.Nodes;
using System.Windows;
using NarutoAutoGUI.ProjectModel;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyPresets(AppLogger logger, string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "presets"));
        var interfacePath = Path.Combine(directory, "interface.json");
        var root = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        var second = root["task"]![0]!.DeepClone();
        second["name"] = "SecondTask";
        root["task"]!.AsArray().Add(second);
        root["pretask"] = JsonNode.Parse("""
            [{"name":"Launch","exec":"fixture","option":["Mode"]},
             {"name":"Other","exec":"fixture"},
             {"name":"Filtered","exec":"fixture","controller":["OtherController"]}]
            """);
        root["preset"] = JsonNode.Parse("""
            [{"name":"daily","label":"日常预设","description":"预设说明","icon":"daily.png","task":[
                {"name":"SecondTask","option":{"Mode":"Default","Nested":"Off","ServerRange":{"server_range":"979"}}},
                {"name":"RealTask","enabled":false,"option":{"Mode":"Default","ServerRange":{"retry_count":"4"}}}]},
             {"name":"startup","task":[],"pretask":[
                {"name":"Other"},{"name":"Launch","option":{"Mode":"Minimal"}},
                {"name":"Missing"},{"name":"Filtered"}]},
             {"name":"clear"}]
            """);
        File.WriteAllText(interfacePath, root.ToJsonString());
        var path = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, path);
        Require(project.Presets.Select(item => item.Name).SequenceEqual(["daily", "startup", "clear"])
            && project.Presets[0].Description == "预设说明" && project.Presets[1].Label == "startup",
            "预设必须保留顺序、名称回退和展示信息。");
        var original = project.ActiveConfigurationId;
        project.AddPretask("Launch");
        project.SetSelectedCase(original, "Mode", "Minimal");
        var daily = project.CreateConfigurationFromPreset("daily");
        Require(project.ActiveConfigurationId == daily && project.Configurations.Count == 2
            && project.SelectedTaskNames.SequenceEqual(["SecondTask"]) && project.SelectedPretaskNames.Count == 0
            && project.Configurations[0].SelectedTasks.SequenceEqual(["RealTask"]),
            "从预设新建必须保留旧配置，按预设顺序选中 enabled 任务，未声明 pretask 时不继承当前启动阶段。");
        var config = project.Configurations.Single(item => item.Id == daily);
        Require(config.Name == "日常预设" && config.ExplicitOptions["Nested"].GetProperty("SelectedCase").GetString()
            == "Off" && config.ExplicitOptions["Mode"].GetProperty("SelectedCase").GetString() == "Default"
            && project.CreateRunStartAttempt().Plan.Items.Count == 1,
            "预设嵌套 switch、select 和 input 必须转换为现有 intent，不继承当前配置的参数，并生成运行计划。");
        project = ProjectPlanModule.Open(directory, path);
        var inputs = project.Configurations[1].ExplicitOptions["ServerRange"].GetProperty("Inputs");
        Require(project.ActiveConfigurationId == daily && project.SelectedTaskNames.SequenceEqual(["SecondTask"])
            && inputs.GetProperty("server_range").GetString() == "979"
            && inputs.GetProperty("retry_count").GetString() == "4",
            "预设配置必须通过现有持久化重开；禁用任务保留选项但不能重新启用，不同任务的 input 字段必须合并。");
        project.SetInputValue(daily, "ServerRange", "retry_count", "5");
        project.CreateConfigurationFromPreset("daily");
        Require(project.Configurations.Single(item => item.Id == daily).ExplicitOptions["ServerRange"]
            .GetProperty("Inputs").GetProperty("retry_count").GetString() == "5",
            "重复套用预设必须创建独立副本，不能覆盖用户编辑。");
        project.CreateConfigurationFromPreset("startup");
        Require(project.CreateRunStartAttempt().Plan.Items.Select(item => item.TaskName)
            .SequenceEqual(["Launch", "Other"]),
            "preset.pretask 必须忽略缺失/不适用项，并按顶层声明顺序执行。");
        project.CreateConfigurationFromPreset("clear");
        Require(project.SelectedItemCount == 0 && project.Configurations[^1].ExplicitOptions.Count == 0,
            "省略 pretask 时只使用项目默认值，不能继承上份配置的启动阶段或参数。");
        Require(Throws<ArgumentException>(() => project.CreateConfigurationFromPreset("Missing")),
            "不存在的预设必须拒绝，不能创建空配置。");
        VerifyPresetFailures(directory, root);
        VerifyPresetMenu(logger, testDirectory, project);
    }

    private static void VerifyPresetFailures(string directory, JsonNode root)
    {
        var path = Path.Combine(directory, "invalid-config.json");
        var interfacePath = Path.Combine(directory, "interface.json");
        string[] invalid = [
            """[{"name":"bad","task":[{"name":"Missing"}]}]""",
            """[{"name":"bad","task":[{"name":"RealTask","option":{"Mode":"Missing"}}]}]""",
            """[{"name":"bad","task":[{"name":"RealTask","option":{"Mode":true}}]}]""",
            """[{"name":"bad","task":[{"name":"RealTask","option":{"Other":"x"}}]}]""",
            """[{"name":"bad","task":[{"name":"RealTask","option":{"ServerRange":{"missing":"x"}}}]}]""",
            """[{"name":"bad","task":[{"name":"RealTask","option":{"ServerRange":{"retry_count":"x"}}}]}]""",
            """
            [{"name":"bad","task":[{"name":"RealTask","option":{"Nested":"On"}},
                {"name":"SecondTask","option":{"Nested":"Off"}}]}]
            """,
            """
            [{"name":"bad","task":[{"name":"RealTask","option":{"ServerRange":{"retry_count":"4"}}},
                {"name":"SecondTask","option":{"ServerRange":{"retry_count":"5"}}}]}]
            """
        ];
        foreach (var preset in invalid) {
            root["preset"] = JsonNode.Parse(preset);
            File.WriteAllText(interfacePath, root.ToJsonString());
            var project = ProjectPlanModule.Open(directory, path);
            var saved = File.ReadAllText(path);
            var active = project.ActiveConfigurationId;
            Require(Throws<InvalidDataException>(() => project.CreateConfigurationFromPreset("bad")),
                "非法预设引用、类型、输入或共享冲突必须拒绝。");
            Require(project.Configurations.Count == 1 && project.ActiveConfigurationId == active
                && File.ReadAllText(path) == saved, "创建失败不得修改内存配置或磁盘文件。");
        }
        string[] malformed = [
            "{}", """[{"name":"bad","task":[],"extra":true}]""",
            """[{"name":"same","task":[]},{"name":"same","task":[]}]""",
            """[{"name":"bad","task":[{"name":"RealTask","enabled":"false"}]}]""",
            """[{"name":"bad","task":[{"name":"RealTask"},{"name":"RealTask"}]}]"""
        ];
        foreach (var preset in malformed) {
            root["preset"] = JsonNode.Parse(preset);
            File.WriteAllText(interfacePath, root.ToJsonString());
            Require(Throws<InvalidDataException>(() => ProjectPlanModule.Open(directory, path)),
                "畸形 preset、重复标识和重复任务必须在读取时拒绝。");
        }
    }

    private static void VerifyPresetMenu(AppLogger logger, string testDirectory, ProjectPlanModule project)
    {
        using var session = new ChildSession.ChildSessionManager(logger);
        var coordinator = new Worker.WorkerCoordinator(logger, Path.Combine(testDirectory, "preset-state"),
            "unused.exe", $"NarutoAutoGUI.Presets.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
        var window = new Views.MainWindow(logger, session, coordinator, WindowEnvironment(logger, session, coordinator),
            () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask);
        try {
            SetOnboardingField(window, "_projectPlan", project);
            InvokeOnboarding(window, "RenderTaskPlan");
            var button = (Button)window.FindName("PresetConfigurationButton");
            Require(button.Visibility == Visibility.Visible, "有预设时必须展示新建入口。");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = button.ContextMenu!;
            Require(menu.Items.Count == 3 && ((MenuItem)menu.Items[0]).Header as string == "日常预设",
                "预设菜单必须按项目顺序显示名称。");
            menu.IsOpen = false;
            var count = project.Configurations.Count;
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(project.Configurations.Count == count + 1 && project.Configurations[^1].Name == "startup",
                "点击预设菜单必须创建并激活普通配置。");
            SetOnboardingField(window, "_pendingOperation", Views.PendingOperation.PreparingEnvironment);
            InvokeOnboarding(window, "RefreshRuntimeControls");
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(!button.IsEnabled && project.Configurations.Count == count + 1,
                "运行环境处理中必须同时锁住预设按钮及已打开的菜单操作。");
            var plain = CreateProjectFixture(Path.Combine(testDirectory, "no-presets"));
            var plainPath = Path.Combine(plain, "config.json");
            SetOnboardingField(window, "_projectPlan", ProjectPlanModule.Open(plain, plainPath));
            InvokeOnboarding(window, "RenderTaskPlan");
            Require(button.Visibility == Visibility.Collapsed, "未声明预设时必须隐藏新建入口。");
        } finally {
            window.AllowClose();
            window.Close();
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }
}
