using System.Text.Json;
using NarutoAutoGUI.ProjectModel;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyFirstConfiguration(string testDirectory, string projectDirectory)
    {
        var path = Path.Combine(testDirectory, "first-use.json");
        var project = ProjectPlanModule.Open(projectDirectory, path);
        if (!project.SelectedTaskNames.SequenceEqual(new[] { "RealTask" })
            || project.Configurations.Single().Name != "配置 1"
            || project.Configurations.Single().ExplicitOptions.Count != 0) {
            throw new InvalidOperationException("首次缺失配置必须预置真实 PI 第一项且不写默认参数。");
        }
        var reopened = ProjectPlanModule.Open(projectDirectory, path);
        if (reopened.ActiveConfigurationId != project.ActiveConfigurationId
            || !reopened.SelectedTaskNames.SequenceEqual(project.SelectedTaskNames)) {
            throw new InvalidOperationException("首次默认任务必须已保存，重开保持身份和任务。");
        }
        project.RemoveTask("RealTask");
        if (ProjectPlanModule.Open(projectDirectory, path).SelectedTaskNames.Count != 0) {
            throw new InvalidOperationException("已有空配置不得自动补入任务。");
        }
        var blockedDirectory = Path.Combine(testDirectory, "blocked-initial-save");
        File.WriteAllText(blockedDirectory, "not a directory");
        var failed = ProjectPlanModule.Open(projectDirectory, Path.Combine(blockedDirectory, "config.json"));
        if (failed.LoadWarning is null || failed.SelectedTaskNames.Count != 0
            || failed.InitializedTaskName is not null) {
            throw new InvalidOperationException("默认保存失败必须保留空工作区并报告警告。");
        }
        var alternate = CreateProjectFixture(Path.Combine(testDirectory, "first-task-order"));
        var interfacePath = Path.Combine(alternate, "interface.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(interfacePath))!;
        var first = json["task"]![0]!.DeepClone();
        first["name"] = "DifferentFirstTask";
        json["task"]!.AsArray().Insert(0, first);
        File.WriteAllText(interfacePath, json.ToJsonString());
        var reordered = ProjectPlanModule.Open(alternate, Path.Combine(alternate, "config.json"));
        if (!reordered.SelectedTaskNames.SequenceEqual(new[] { "DifferentFirstTask" })) {
            throw new InvalidOperationException("首次默认任务必须跟随 PI 数组顺序。");
        }
    }

    private static void VerifyInvalidConfigurationIsolation(string testDirectory, string projectDirectory)
    {
        var path = Path.Combine(testDirectory, "invalid-intent.json");
        File.WriteAllText(path, """
            {"SchemaVersion":1,"SelectedTasks":["RemovedTask","RealTask"],"ExplicitOptions":{
              "ServerRange":{"Inputs":{"server_range":"invalid"}}}}
            """);
        var project = ProjectPlanModule.Open(projectDirectory, path);
        var invalid = project.ActiveConfigurationId;
        var valid = project.CreateConfiguration();
        project.AddTask("RealTask");
        _ = project.CreateRunStartAttempt();
        project.ActivateConfiguration(invalid);
        project.RenameConfiguration(invalid, "仍可管理");
        var editor = project.GetConfiguration("RealTask");
        if (editor.GlobalOptions.Single().Inputs[0].Value != "invalid") {
            throw new InvalidOperationException("语义失效的显式参数仍应可查看，不应清空。");
        }
        project.RemoveTask("RemovedTask");
        project.SetInputValue(invalid, "ServerRange", "server_range", "978");
        _ = project.CreateRunStartAttempt();
        project.DeleteConfiguration(invalid);
        if (project.ActiveConfigurationId != valid) {
            throw new InvalidOperationException("失效配置修正或删除不能影响其他配置。");
        }
    }

    private static void VerifyConfigurationTabs(AppLogger logger, string testDirectory, string projectDirectory)
    {
        using var session = new ChildSession.ChildSessionManager(logger);
        var coordinator = new Worker.WorkerCoordinator(logger, Path.Combine(testDirectory, "tabs-state"), "unused.exe",
            $"NarutoAutoGUI.Tabs.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
        var window = new Views.MainWindow(logger, session, new ChildSession.ChildSessionProgramService(logger),
            coordinator, operation => operation(), () => Task.CompletedTask);
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var project = ProjectPlanModule.Open(projectDirectory, Path.Combine(testDirectory, "tabs.json"));
        try {
            typeof(Views.MainWindow).GetField("_projectPlan", flags)!.SetValue(window, project);
            typeof(Views.MainWindow).GetMethod("RenderTaskPlan", flags)!.Invoke(window, null);
            var tabs = window.FindName("ConfigurationTabs") as System.Windows.Controls.TabControl
                ?? throw new InvalidOperationException("任务工作区需要配置 Tab 控件。");
            var add = (System.Windows.Controls.Button)window.FindName("NewConfigurationButton");
            add.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (project.Configurations.Count != 2 || tabs.Items.Count != 2 || tabs.SelectedIndex != 1) {
                throw new InvalidOperationException("新建按钮必须创建并激活第二份空配置。");
            }
            tabs.SelectedIndex = 0;
            if (project.ActiveConfigurationId != project.Configurations[0].Id) {
                throw new InvalidOperationException("Tab 选择必须保存 Active。");
            }
            project.AddTask("RealTask");
            typeof(Views.MainWindow).GetField("_expandedTaskName", flags)!.SetValue(window, "RealTask");
            typeof(Views.MainWindow).GetMethod("RenderTaskPlan", flags)!.Invoke(window, null);
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            var retry = ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBox>()
                .Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == "Retry count");
            retry.Text = "3";
            retry.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, 0, retry, tabs) {
                RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent
            });
            if (project.Configurations[0].ExplicitOptions["ServerRange"].GetProperty("Inputs")
                .GetProperty("retry_count").GetString() != "3") {
                throw new InvalidOperationException("显式提交等于默认值的输入也必须保留。");
            }
            var input = ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBox>()
                .Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == "Server");
            input.Text = "979";
            tabs.SelectedIndex = 1;
            input.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, 0, input, tabs) {
                RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent
            });
            if (project.Configurations[0].ExplicitOptions["ServerRange"].GetProperty("Inputs")
                    .GetProperty("server_range").GetString() != "979"
                || project.Configurations[1].ExplicitOptions.Count != 0) {
                throw new InvalidOperationException("旧控件失焦只能写回其来源配置。");
            }
            typeof(Views.MainWindow).GetField("_busy", flags)!.SetValue(window, true);
            typeof(Views.MainWindow).GetMethod("UpdateCommandAvailability", flags)!.Invoke(window, null);
            add.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (tabs.IsEnabled || add.IsEnabled || project.Configurations.Count != 2) {
                throw new InvalidOperationException("Start in-flight 必须锁住新增和 Tab 操作。");
            }
        } finally {
            window.AllowClose();
            window.Close();
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    private static IEnumerable<System.Windows.DependencyObject> ConfigurationDescendants(
        System.Windows.DependencyObject parent)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent)
            .OfType<System.Windows.DependencyObject>()) {
            yield return child;
            foreach (var descendant in ConfigurationDescendants(child)) {
                yield return descendant;
            }
        }
    }

    private static void VerifyConfigurationFallback(string testDirectory, string projectDirectory)
    {
        var directory = Path.Combine(testDirectory, "fallback");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "maanop-config.json");
        var invalid = System.Text.Encoding.UTF8.GetBytes("{ broken original\r\n");
        File.WriteAllBytes(path, invalid);
        var project = ProjectPlanModule.Open(projectDirectory, path);
        var id = project.ActiveConfigurationId;
        if (project.SelectedTaskNames.Count != 0 || project.Configurations.Single().Name != "配置 1"
            || !File.ReadAllBytes(path).SequenceEqual(invalid)
            || Directory.GetFiles(directory).Length != 1) {
            throw new InvalidOperationException("异常文件加载必须提供空配置且不覆盖或提前备份。");
        }
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            try {
                project.AddTask("RealTask");
                throw new InvalidOperationException("备份失败必须拒绝本次保存。");
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                if (project.SelectedTaskNames.Count != 0 || project.ActiveConfigurationId != id) {
                    throw new InvalidOperationException("失败不能发布尚未保存的配置状态。");
                }
            }
        }
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            try {
                project.AddTask("RealTask");
                throw new InvalidOperationException("备份成功后替换失败也必须报告保存失败。");
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                if (!File.ReadAllBytes(path).SequenceEqual(invalid) || project.SelectedTaskNames.Count != 0) {
                    throw new InvalidOperationException("替换失败不能破坏异常原文件或提交内存修改。");
                }
            }
        }
        project.AddTask("RealTask");
        var backup = Directory.GetFiles(directory, "maanop-config.invalid-*.json").Single();
        if (!File.ReadAllBytes(backup).SequenceEqual(invalid)) {
            throw new InvalidOperationException("异常备份必须保留原始 bytes。");
        }
        project.SetInputValue("ServerRange", "server_range", "978");
        if (Directory.GetFiles(directory, "*.invalid-*.json").Length != 1) {
            throw new InvalidOperationException("正常保存不得产生异常备份历史。");
        }
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["ActiveConfigurationId"] = Guid.NewGuid().ToString();
        File.WriteAllText(path, json.ToJsonString());
        project = ProjectPlanModule.Open(projectDirectory, path);
        if (project.ActiveConfigurationId != id || project.SelectedTaskNames.Count != 1) {
            throw new InvalidOperationException("无效 Active 应选择第一份而不是清空用户配置。");
        }
        File.WriteAllText(path, """{"SchemaVersion":2,"Configurations":[]}""");
        project = ProjectPlanModule.Open(projectDirectory, path);
        if (project.Configurations.Count != 1 || project.SelectedTaskNames.Count != 0) {
            throw new InvalidOperationException("空列表必须生成一份空配置。");
        }
        foreach (var corrupt in new[] {
            "{}", "{\"SchemaVersion\":99}", "{\"SchemaVersion\":\"2\"}",
            "{\"SchemaVersion\":2,\"SchemaVersion\":2,\"Configurations\":[]}",
            "{\"SchemaVersion\":2,\"Configurations\":null}"
        }) {
            File.WriteAllText(path, corrupt);
            project = ProjectPlanModule.Open(projectDirectory, path);
            if (project.Configurations.Count != 1 || project.LoadWarning is null
                || File.ReadAllText(path) != corrupt) {
                throw new InvalidOperationException("异常 schema 必须保留原文件并提供空工作区。");
            }
        }
    }

    private static void VerifyIndependentConfigurations(string testDirectory, string projectDirectory)
    {
        var path = Path.Combine(testDirectory, "independent.json");
        var project = ProjectPlanModule.Open(projectDirectory, path);
        var first = project.ActiveConfigurationId;
        project.AddTask("RealTask");
        project.SetInputValue(first, "ServerRange", "server_range", "978");
        var second = project.CreateConfiguration();
        if (second == first || project.SelectedTaskNames.Count != 0
            || project.Configurations[1].ExplicitOptions.Count != 0) {
            throw new InvalidOperationException("新配置必须有新身份且没有继承任务或参数。");
        }
        project.AddTask("RealTask");
        project.SetInputValue(second, "ServerRange", "server_range", "1012");
        project.SetInputValue(first, "ServerRange", "server_range", "979");
        if (project.CreateRunStartAttempt().Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                .GetProperty("server_range").GetString() != "1012") {
            throw new InvalidOperationException("来源配置的迟到编辑不能修改当前配置。");
        }
        project.RenameConfiguration(first, "小号");
        project.RenameConfiguration(second, "小号");
        project.ActivateConfiguration(first);
        if (project.CreateRunStartAttempt().Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                .GetProperty("server_range").GetString() != "979" || project.AddTask("RealTask")) {
            throw new InvalidOperationException("配置必须隔离参数且同配置不能重复 task。");
        }
        project = ProjectPlanModule.Open(projectDirectory, path);
        if (project.ActiveConfigurationId != first || project.Configurations.Count != 2
            || project.Configurations.Any(item => item.Name != "小号")) {
            throw new InvalidOperationException("重开必须恢复同名配置和 Active。");
        }
        var original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            try {
                project.ActivateConfiguration(second);
                throw new InvalidOperationException("文件不能替换时切换不能宣称成功。");
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                if (project.ActiveConfigurationId != first || !File.ReadAllBytes(path).SequenceEqual(original)) {
                    throw new InvalidOperationException("保存失败必须保持 Active 与原文件。");
                }
            }
        }
        try {
            project.RenameConfiguration(second, "  ");
            throw new InvalidOperationException("空白配置名称必须被拒绝。");
        } catch (InvalidDataException) {
            // Rejected edits must leave persisted state unchanged.
        }
        project.DeleteConfiguration(first);
        if (project.ActiveConfigurationId != second || project.DeleteConfiguration(second)) {
            throw new InvalidOperationException("删除当前配置应选邻居且不能删除最后一份。");
        }
        var third = project.CreateConfiguration();
        var fourth = project.CreateConfiguration();
        project.DeleteConfiguration(fourth);
        if (project.ActiveConfigurationId != third) {
            throw new InvalidOperationException("删除当前配置应优先选左邻。");
        }
        project.DeleteConfiguration(second);
        if (project.ActiveConfigurationId != third) {
            throw new InvalidOperationException("删除非当前配置不应改变 Active。");
        }
    }

    private static void VerifyConfigurationMigration(string testDirectory, string projectDirectory)
    {
        var path = Path.Combine(testDirectory, "migration.json");
        File.WriteAllText(path, """
            {"SchemaVersion":1,"SelectedTasks":["RealTask"],"ExplicitOptions":{
              "ServerRange":{"Inputs":{"server_range":"978"}},
              "Mode":{"SelectedCase":"Minimal"},"Nested":{"SelectedCase":"Off"},
              "OldOption":{"SelectedCase":"Keep"}}}
            """);
        var project = ProjectPlanModule.Open(projectDirectory, path);
        using var saved = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = saved.RootElement;
        if (root.GetProperty("SchemaVersion").GetInt32() != 2) {
            throw new InvalidOperationException("V1 应在首次加载时包装为 V2。");
        }
        var configuration = root.GetProperty("Configurations")[0];
        var id = configuration.GetProperty("Id").GetGuid();
        if (id == Guid.Empty || root.GetProperty("ActiveConfigurationId").GetGuid() != id
            || configuration.GetProperty("Name").GetString() != "配置 1"
            || configuration.GetProperty("ExplicitOptions").GetProperty("OldOption")
                .GetProperty("SelectedCase").GetString() != "Keep"
            || project.CreateRunStartAttempt().Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                .GetProperty("server_range").GetString() != "978") {
            throw new InvalidOperationException("V1 包装必须保留原始意图并激活稳定身份。");
        }
        _ = ProjectPlanModule.Open(projectDirectory, path);
        using var reopened = JsonDocument.Parse(File.ReadAllBytes(path));
        if (reopened.RootElement.GetProperty("ActiveConfigurationId").GetGuid() != id) {
            throw new InvalidOperationException("重新打开 V2 不应重新生成配置 Id。");
        }
    }
}
