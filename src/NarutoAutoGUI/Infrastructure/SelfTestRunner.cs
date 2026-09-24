using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    internal static int Run(bool projectOnly = false, bool supportOnly = false)
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"NarutoAutoGUI-self-test-{Guid.NewGuid():N}");

        try {
            Directory.CreateDirectory(testDirectory);
            var logDirectory = Path.Combine(testDirectory, "logs");
            using var logger = new AppLogger(logDirectory);
            VerifyTerminalNotifications();
            VerifyDiagnosticPackage(logger, testDirectory);
            VerifyDeclarativeSettings(logger, testDirectory);
            VerifyDiagnosticSettings(logger, testDirectory);
            VerifyNavigationPreference(logger, testDirectory);
            if (supportOnly) {
                Console.WriteLine("SUPPORT SELF-TEST PASS");
                return 0;
            }
            var projectDirectory = CreateProjectFixture(testDirectory);
            VerifyFirstConfiguration(testDirectory, projectDirectory);
            VerifyConfigurationMigration(testDirectory, projectDirectory);
            VerifyIndependentConfigurations(testDirectory, projectDirectory);
            VerifyConfigurationFallback(testDirectory, projectDirectory);
            VerifyInvalidConfigurationIsolation(testDirectory, projectDirectory);
            VerifyConfigurationTabs(logger, testDirectory, projectDirectory);
            VerifyProjectPlan(testDirectory, projectDirectory);
            VerifyTaskCatalogVariants(testDirectory, projectDirectory);
            VerifyTaskGroups(testDirectory, projectDirectory);
            VerifyTaskShelfLayout(logger, testDirectory);
            VerifyOnboarding(logger, testDirectory);
            if (projectOnly) {
                Console.WriteLine("PROJECT SELF-TEST PASS");
                return 0;
            }
            VerifyGameLaunchProfile(logger, testDirectory);
            VerifyLauncherHandoff();
            VerifyRdpClientClsid();
            VerifyTaskDescriptionMarkup();
            VerifyResponsiveOptionLayout();
            VerifyUnsupportedProjectConstraints(testDirectory, projectDirectory);
            VerifyInvalidProjectInterfaces(testDirectory, projectDirectory);
            VerifyProtocolFrame();
            VerifyPreviewProtocol();
            PreviewPresentationSelfTest.Run();
            VerifyMinimizedPreview(logger, testDirectory);
            VerifyWorkerLogSequenceTracker();
            VerifyRunLogRouting(logger);
            VerifyHomePresentation();
            VerifyEndedSessionControls(logger, testDirectory, projectDirectory);
            Task.Run(() => WorkerCoordinatorSelfTest.RunAsync(
                logger, testDirectory, projectDirectory,
                Path.Combine(testDirectory, "maanop-config.json"))).GetAwaiter().GetResult();

            logger.Debug("self-test-debug");
            logger.Info("self-test-info");
            logger.Dispose();
            var logText = string.Join(
                Environment.NewLine, Directory.EnumerateFiles(logDirectory, "*.log").Select(File.ReadAllText));
            if (!logText.Contains("[DEBUG] self-test-debug", StringComparison.Ordinal)
                || !logText.Contains("[INFO] self-test-info", StringComparison.Ordinal)
                || !logText.Contains("[maanop.run] 用户日志", StringComparison.Ordinal)
                || !logText.Contains("[runtime.task] 用户日志", StringComparison.Ordinal)
                || !logText.Contains("GUI diagnostic only", StringComparison.Ordinal)) {
                throw new InvalidOperationException("DEBUG+ 文件日志验证失败。");
            }

            Console.WriteLine(
                "SELF-TEST PASS: fixed Naruto game launch profile (AppData-derived launcher, fixed AppId); "
                + "RDP MsRdpClient10 CLSID; "
                + "PI default/explicit resolver; "
                + "ordered pipeline override; nested dormant intent; "
                + "Win32 PI validation; unsupported PI scope/constraint fail-closed; "
                + "PI structure/default/graph validation; typed input validation; "
                + "task catalog/description; ordered task plan persistence; responsive option layout; "
                + "MaaNOP Config v2/migration/independent tabs; onboarding tour; RunPlan digest; "
                + "IPC framing; preview schema; "
                + "log sequence tracking/recovery; Worker Instance replacement; "
                + "run-log routing; DEBUG+ file logging");
            return 0;
        } catch (Exception exception) {
            Console.Error.WriteLine($"SELF-TEST FAIL: {exception}");
            return 1;
        } finally {
            try {
                if (Directory.Exists(testDirectory)) {
                    Directory.Delete(testDirectory, recursive: true);
                }
            } catch {
                // The isolated temporary directory can be cleaned by the OS later.
            }
        }
    }

    private static void VerifyGameLaunchProfile(AppLogger logger, string testDirectory)
    {
        var customRoot = Path.Combine(testDirectory, "AppDataRoaming");
        Directory.CreateDirectory(customRoot);
        var profile = NarutoGameLaunchProfile.Resolve(customRoot);
        var expectedPath = Path.Combine(customRoot, "Tencent", "QQMicroGameBox", "Launch.exe");
        if (profile.AppId != "1103286479"
            || profile.Arguments != "-/appid:1103286479"
            || profile.ExecutablePath != expectedPath) {
            throw new InvalidOperationException("NarutoGameLaunchProfile path/AppId/arguments 解析验证失败。");
        }

        try {
            NarutoGameLaunchProfile.ResolveExisting(logger, applicationDataRoot: customRoot);
            throw new InvalidOperationException("NarutoGameLaunchProfile.ResolveExisting(logger) 未在启动器缺失时报错。");
        } catch (FileNotFoundException exception) when (
            exception.Message.Contains("未检测到火影忍者 Online 微端启动器", StringComparison.Ordinal)) {
            // Expected: actionable error referencing QQ 游戏平台 installation, not user configuration.
        }

        var productionProfile = NarutoGameLaunchProfile.Resolve();
        var productionRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var expected = Path.Combine(productionRoot, "Tencent", "QQMicroGameBox", "Launch.exe");
        if (!string.Equals(productionProfile.ExecutablePath, expected, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException("Production launch profile 路径推导与当前用户 ApplicationData 不一致。");
        }
    }

    private static void VerifyLauncherHandoff()
    {
        (uint ProcessId, uint SessionId, string Name)[] processes = [
            (101, 1, "Launch.exe"), (102, 1, "QQMicroGameBox.exe"), (103, 25, "unrelated.exe")
        ];
        var clientName = NarutoGameLaunchProfile.ClientProcessName;
        if (ChildSessionProgramService.FindLaunchProcess(processes, 25, "Launch.exe", clientName).ProcessId != 0) {
            throw new InvalidOperationException("启动验证不得接受其他 Session 或无关进程。");
        }

        var handedOff = processes.Append((104u, 25u, "qqmicrogamebox.EXE"));
        if (ChildSessionProgramService.FindLaunchProcess(handedOff, 25, "Launch.exe", clientName).ProcessId != 104
            || ChildSessionProgramService.FindLaunchProcess(handedOff, 25, "Launch.exe", null).ProcessId != 0) {
            throw new InvalidOperationException("启动器退出后应接受当前 Session 的微端，普通程序不得接受微端替代。");
        }

        var launcher = processes.Append((105u, 25u, "launch.EXE"));
        if (ChildSessionProgramService.FindLaunchProcess(launcher, 25, "Launch.exe", clientName).ProcessId != 105) {
            throw new InvalidOperationException("启动器仍存活时应保留原有启动验证行为。");
        }
    }

    private static void VerifyRdpClientClsid()
    {
        const string expectedClsid = "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";
        const string prohibitedClsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";

        if (!string.Equals(RdpActiveXHost.RdpClientClsid, expectedClsid, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(
                $"RDP ActiveX CLSID 预期为 MsRdpClient10 ({expectedClsid})，实际为 {RdpActiveXHost.RdpClientClsid}。");
        }

        if (string.Equals(RdpActiveXHost.RdpClientClsid, prohibitedClsid, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException("RDP ActiveX CLSID 不能使用 Win11 专用的 MsRdpClient11 (v11)。");
        }

        if (!Guid.TryParse(RdpActiveXHost.RdpClientClsid, out var clsidGuid) || clsidGuid == Guid.Empty) {
            throw new InvalidOperationException("RDP ActiveX CLSID 不是有效 GUID。");
        }
    }

    private static void VerifyProjectPlan(string testDirectory, string projectDirectory)
    {
        var configPath = Path.Combine(testDirectory, "maanop-config.json");
        var project = ProjectPlanModule.Open(projectDirectory, configPath);
        if (project.Tasks.Count != 1
            || project.Tasks[0].Name != "RealTask"
            || project.Tasks[0].Label != "Real task"
            || !project.Tasks[0].Description.StartsWith("A long task description", StringComparison.Ordinal)) {
            throw new InvalidOperationException("PI task catalog 验证失败。");
        }

        _ = project.AddTask("RealTask");
        using (var configDocument = JsonDocument.Parse(File.ReadAllBytes(configPath))) {
            var root = configDocument.RootElement;
            if (root.GetProperty("SchemaVersion").GetInt32() != MaaNopConfig.CurrentSchemaVersion
                || root.GetProperty("Configurations")[0].GetProperty("SelectedTasks")[0].GetString() != "RealTask"
                || root.GetProperty("Configurations")[0].GetProperty("ExplicitOptions").EnumerateObject().Any()) {
                throw new InvalidOperationException(
                    $"MaaNOP Config SchemaVersion {MaaNopConfig.CurrentSchemaVersion} 验证失败。");
            }
        }

        var defaultConfiguration = project.GetConfiguration("RealTask");
        var defaultServer = defaultConfiguration.GlobalOptions.Single();
        var defaultMode = defaultConfiguration.TaskOptions.Single();
        if (defaultServer.Inputs.Single(input => input.Name == "server_range").Value != "978-1012"
            || defaultServer.IsExplicit
            || defaultMode.SelectedCase != "Default"
            || defaultMode.ActiveChildren.Single().SelectedCase != "On") {
            throw new InvalidOperationException("PI option editor 默认视图验证失败。");
        }

        var defaultAttempt = project.CreateRunStartAttempt();
        var defaultPipeline = defaultAttempt.Plan.Items[0].PipelineOverride;
        if (defaultAttempt.Plan.Items.Count != 1
            || defaultAttempt.Plan.Items[0].TaskName != "RealTask"
            || defaultAttempt.Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                .GetProperty("server_range").GetString() != "978-1012"
            || defaultAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Mode").GetString() != "Default"
            || defaultAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Nested").GetString() != "On"
            || defaultPipeline.ValueKind != JsonValueKind.Array
            || defaultPipeline.GetArrayLength() != 4
            || !defaultPipeline[0].GetProperty("ScopeOrder").GetProperty("task").GetBoolean()
            || !defaultPipeline[1].GetProperty("ScopeOrder").GetProperty("global").GetBoolean()
            || !defaultPipeline[2].GetProperty("ScopeOrder").GetProperty("task_option").GetBoolean()
            || !defaultPipeline[3].GetProperty("ScopeOrder").GetProperty("nested").GetBoolean()
            || defaultPipeline[1].GetProperty("TypedValues").GetProperty("retry_count").GetInt32() != 3
            || !defaultPipeline[1].GetProperty("TypedValues").GetProperty("enabled").GetBoolean()
            || defaultPipeline[1].GetProperty("TypedValues").GetProperty("summary").GetString()
                != "978-1012:3:true"
            || defaultPipeline[0].GetProperty("SelfTestEntry").TryGetProperty("mode", out _)
            || defaultPipeline[2].GetProperty("SelfTestEntry").TryGetProperty("enabled", out _)
            || defaultAttempt.PlanDigest != CanonicalDigest.ComputePlanDigestV1(defaultAttempt.Plan)) {
            throw new InvalidOperationException("正式 PI Resolver / RunPlan / planDigest 验证失败。");
        }

        project.SetInputValue("ServerRange", "server_range", "978");
        project.SetSelectedCase("Nested", "Off");
        using (var configDocument = JsonDocument.Parse(File.ReadAllBytes(configPath))) {
            var explicitOptions = configDocument.RootElement.GetProperty("Configurations")[0]
                .GetProperty("ExplicitOptions");
            if (explicitOptions.GetProperty("ServerRange").GetProperty("Inputs")
                    .GetProperty("server_range").GetString() != "978"
                || explicitOptions.GetProperty("Nested").GetProperty("SelectedCase")
                    .GetString() != "Off") {
                throw new InvalidOperationException("MaaNOP Config explicit option 序列化验证失败。");
            }
        }

        var explicitAttempt = project.CreateRunStartAttempt();
        var explicitPipeline = explicitAttempt.Plan.Items[0].PipelineOverride;
        var explicitServerRange = explicitPipeline[1]
            .GetProperty("ParseServer").GetProperty("recognition").GetProperty("param")
            .GetProperty("custom_recognition_param").GetString();
        if (explicitAttempt.Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                .GetProperty("server_range").GetString() != "978"
            || explicitAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Nested").GetString() != "Off"
            || explicitServerRange != "978"
            || explicitPipeline.GetArrayLength() != 4
            || explicitPipeline[3].GetProperty("SelfTestEntry").GetProperty("nested").GetBoolean()
            || explicitPipeline[1].GetProperty("TypedValues").GetProperty("summary").GetString()
                != "978:3:true"
            || explicitAttempt.PlanDigest == defaultAttempt.PlanDigest) {
            throw new InvalidOperationException("PI explicit input/switch resolution 验证失败。");
        }

        project.SetSelectedCase("Mode", "Minimal");
        var dormantConfiguration = project.GetConfiguration("RealTask");
        if (dormantConfiguration.TaskOptions.Single().ActiveChildren.Count != 0) {
            throw new InvalidOperationException("PI nested option active graph 验证失败。");
        }
        using (var configDocument = JsonDocument.Parse(File.ReadAllBytes(configPath))) {
            if (configDocument.RootElement.GetProperty("Configurations")[0].GetProperty("ExplicitOptions")
                    .GetProperty("Nested").GetProperty("SelectedCase").GetString() != "Off") {
                throw new InvalidOperationException("MaaNOP Config dormant intent 保留验证失败。");
            }
        }
        var dormantAttempt = project.CreateRunStartAttempt();
        if (dormantAttempt.Plan.Items[0].ResolvedOptions.TryGetProperty("Nested", out _)
            || dormantAttempt.Plan.Items[0].PipelineOverride.GetArrayLength() != 3) {
            throw new InvalidOperationException("Dormant option 不应进入 Run Plan。");
        }

        project.SetSelectedCase("Mode", "Default");
        var restoredAttempt = project.CreateRunStartAttempt();
        if (restoredAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Nested").GetString() != "Off"
            || restoredAttempt.Plan.Items[0].PipelineOverride[3]
                .GetProperty("SelfTestEntry").GetProperty("nested").GetBoolean()) {
            throw new InvalidOperationException("PI nested dormant intent 恢复验证失败。");
        }

        try {
            project.SetInputValue("ServerRange", "server_range", "not-a-server");
            throw new InvalidOperationException("PI input verify 未拒绝非法显式值。");
        } catch (InvalidDataException) {
            // Expected: invalid edits are not persisted.
        }
        VerifyRejectedInputEdit(project, "retry_count", "3.5");
        VerifyRejectedInputEdit(project, "enabled", "not-a-bool");
        var retainedInputs = project.GetConfiguration("RealTask").GlobalOptions.Single().Inputs
            .ToDictionary(input => input.Name, input => input.Value, StringComparer.Ordinal);
        if (retainedInputs["server_range"] != "978"
            || retainedInputs["retry_count"] != "3"
            || retainedInputs["enabled"] != "true") {
            throw new InvalidOperationException("非法显式值不应覆盖最后一次合法配置。");
        }

        var resetProject = ProjectPlanModule.Open(
            projectDirectory, Path.Combine(testDirectory, "default-maanop-config.json"));
        _ = resetProject.AddTask("RealTask");
        var resetAttempt = resetProject.CreateRunStartAttempt();
        if (resetAttempt.Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                .GetProperty("server_range").GetString() != "978-1012"
            || resetAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Mode").GetString() != "Default"
            || resetAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Nested").GetString() != "On") {
            throw new InvalidOperationException("PI option 跟随项目默认验证失败。");
        }
    }

    private static void VerifyTaskCatalogVariants(string testDirectory, string sourceProjectDirectory)
    {
        var sourceInterface = File.ReadAllText(Path.Combine(sourceProjectDirectory, "interface.json"));
        var root = JsonNode.Parse(sourceInterface)?.AsObject()
                   ?? throw new InvalidOperationException("PI task catalog fixture 解析失败。");
        var tasks = root["task"]!.AsArray();
        tasks.Add(new JsonObject {
            ["name"] = "LongLabelTask",
            ["label"] = "A deliberately long task label that must remain available without truncation",
            ["description"] = null,
            ["entry"] = "LongLabelEntry",
            ["option"] = new JsonArray()
        });
        tasks.Add(new JsonObject {
            ["name"] = "MissingDescriptionTask",
            ["label"] = "Missing description",
            ["entry"] = "MissingDescriptionEntry",
            ["option"] = new JsonArray()
        });
        tasks.Add(new JsonObject {
            ["name"] = "WhitespaceDescriptionTask",
            ["label"] = "Whitespace description",
            ["description"] = "   ",
            ["entry"] = "WhitespaceDescriptionEntry",
            ["option"] = new JsonArray()
        });

        var projectDirectory = Path.Combine(testDirectory, "task-catalog-variants");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "interface.json"), root.ToJsonString());
        var configPath = Path.Combine(projectDirectory, "maanop-config.json");
        var project = ProjectPlanModule.Open(projectDirectory, configPath);
        if (project.Tasks.Count != 4
            || project.Tasks.Select(task => task.Name).SequenceEqual(
                ["RealTask", "LongLabelTask", "MissingDescriptionTask", "WhitespaceDescriptionTask"]) is false
            || project.Tasks[1].Label.Length < 60
            || project.Tasks[1].Description.Length != 0
            || project.Tasks[2].Description.Length != 0
            || !string.IsNullOrWhiteSpace(project.Tasks[3].Description)) {
            throw new InvalidOperationException("PI task 顺序、长 label 或可选 description 验证失败。");
        }

        _ = project.AddTask("RealTask");
        project.SetSelectedCase("Mode", "Minimal");
        _ = project.RemoveTask("RealTask");
        _ = project.AddTask("LongLabelTask");
        if (project.GetConfiguration("LongLabelTask").TaskOptions.Count != 0) {
            throw new InvalidOperationException("无 option task 不应生成 task editor。");
        }
        _ = project.RemoveTask("LongLabelTask");
        _ = project.AddTask("RealTask");
        if (project.GetConfiguration("RealTask").TaskOptions.Single().SelectedCase != "Minimal") {
            throw new InvalidOperationException("task selection 切换未保留显式 option intent。");
        }

        if (!project.AddTask("LongLabelTask")
            || !project.AddTask("MissingDescriptionTask")
            || !project.AddTask("WhitespaceDescriptionTask")
            || project.AddTask("RealTask")) {
            throw new InvalidOperationException("执行计划添加或重复任务约束验证失败。");
        }
        _ = project.MoveTask("WhitespaceDescriptionTask", 0);
        _ = project.MoveTask("MissingDescriptionTask", 1);
        var expected = new[] {
            "WhitespaceDescriptionTask", "MissingDescriptionTask", "RealTask", "LongLabelTask"
        };
        if (!project.SelectedTaskNames.SequenceEqual(expected)) {
            throw new InvalidOperationException("执行计划 reorder 结果验证失败。");
        }
        var attempt = project.CreateRunStartAttempt();
        if (!attempt.Plan.Items.Select(item => item.TaskName).SequenceEqual(expected)) {
            throw new InvalidOperationException("Run Plan 未保持 SelectedTasks 执行顺序。");
        }

        var restored = ProjectPlanModule.Open(projectDirectory, configPath);
        if (!restored.SelectedTaskNames.SequenceEqual(expected)
            || restored.GetConfiguration("LongLabelTask").TaskOptions.Count != 0) {
            throw new InvalidOperationException("执行计划保存/恢复顺序验证失败。");
        }
        if (!restored.RemoveTask("MissingDescriptionTask")
            || restored.RemoveTask("MissingDescriptionTask")
            || !restored.SelectedTaskNames.SequenceEqual(
                ["WhitespaceDescriptionTask", "RealTask", "LongLabelTask"])) {
            throw new InvalidOperationException("执行计划删除与顺序保持验证失败。");
        }
        using var configDocument = JsonDocument.Parse(File.ReadAllBytes(configPath));
        var persisted = configDocument.RootElement.GetProperty("Configurations")[0].GetProperty("SelectedTasks")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        if (!persisted.SequenceEqual(new[] { "WhitespaceDescriptionTask", "RealTask", "LongLabelTask" })) {
            throw new InvalidOperationException("SelectedTasks 持久化顺序验证失败。");
        }
    }

    private static void VerifyTaskGroups(string testDirectory, string sourceProjectDirectory)
    {
        var source = File.ReadAllText(Path.Combine(sourceProjectDirectory, "interface.json"));
        var directory = Path.Combine(testDirectory, "task-groups");
        Directory.CreateDirectory(directory);
        var interfacePath = Path.Combine(directory, "interface.json");
        var configPath = Path.Combine(directory, "config.json");
        File.WriteAllText(interfacePath, source);
        var baseline = ProjectPlanModule.Open(directory, configPath);
        if (baseline.Groups.Count != 0 || baseline.Tasks.Any(task => task.Groups.Count != 0)) {
            throw new InvalidOperationException("未声明 group 的 PI 应提供空分组。");
        }
        var before = baseline.CreateRunStartAttempt().Plan;
        var savedConfig = File.ReadAllText(configPath);
        var root = JsonNode.Parse(source)!.AsObject();
        root["group"] = new JsonArray(
            new JsonObject {
                ["name"] = "daily", ["label"] = "日常", ["description"] = "日常任务说明",
                ["icon"] = "groups/daily.png", ["default_expand"] = false
            },
            new JsonObject { ["name"] = "battle" });
        root["task"]![0]!["group"] = new JsonArray("daily", "battle", "unknown");
        File.WriteAllText(interfacePath, root.ToJsonString());
        var grouped = ProjectPlanModule.Open(directory, configPath);
        if (!grouped.Groups.Select(group => group.Name).SequenceEqual(["daily", "battle"])
            || grouped.Groups[0] != new ProjectTaskGroup("daily", "日常", "日常任务说明", "groups/daily.png", false)
            || grouped.Groups[1] != new ProjectTaskGroup("battle", "battle", "", null, true)
            || !grouped.Tasks[0].Groups.SequenceEqual(["daily", "battle", "unknown"])) {
            throw new InvalidOperationException("group 展示元数据、默认值、顺序或多组引用丢失。");
        }
        var after = grouped.CreateRunStartAttempt().Plan;
        if (before.RuntimeProfileDigest != after.RuntimeProfileDigest
            || before.Items[0].TaskName != after.Items[0].TaskName
            || before.Items[0].Entry != after.Items[0].Entry
            || before.Items[0].PipelineOverride.GetRawText() != after.Items[0].PipelineOverride.GetRawText()
            || File.ReadAllText(configPath) != savedConfig) {
            throw new InvalidOperationException("分组元数据不应改变执行内容或用户配置。");
        }
        root["group"] = new JsonArray();
        root["task"]![0]!["group"] = new JsonArray();
        File.WriteAllText(interfacePath, root.ToJsonString());
        var empty = ProjectPlanModule.Open(directory, configPath);
        if (empty.Groups.Count != 0 || empty.Tasks[0].Groups.Count != 0) {
            throw new InvalidOperationException("空 group 数组解析失败。");
        }

        VerifyRejectedProjectInterface(testDirectory, source, "invalid-group-array", "$.group",
            node => node["group"] = "daily");
        VerifyRejectedProjectInterface(testDirectory, source, "invalid-group-name", "name",
            node => node["group"] = new JsonArray(new JsonObject { ["name"] = "" }));
        VerifyRejectedProjectInterface(testDirectory, source, "duplicate-group", "重复 group.name",
            node => node["group"] = new JsonArray(
                new JsonObject { ["name"] = "daily" }, new JsonObject { ["name"] = "daily" }));
        VerifyRejectedProjectInterface(testDirectory, source, "invalid-group-expand", "default_expand",
            node => node["group"] = new JsonArray(
                new JsonObject { ["name"] = "daily", ["default_expand"] = "false" }));
        VerifyRejectedProjectInterface(testDirectory, source, "invalid-task-group", "$.task[0].group",
            node => node["task"]![0]!["group"] = "daily");
        VerifyRejectedProjectInterface(testDirectory, source, "invalid-task-group-item", "$.task[0].group",
            node => node["task"]![0]!["group"] = new JsonArray(123));
    }

    private static void VerifyTaskShelfLayout(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "grouped-shelf"), (window, directory) => {
            var workspace = (System.Windows.FrameworkElement)window.FindName("TaskWorkspacePanel");
            var shelf = (System.Windows.FrameworkElement)window.FindName("TaskShelfContent");
            var catalog = (System.Windows.Controls.StackPanel)window.FindName("AvailableTasksPanel");
            var catalogScroll = (System.Windows.Controls.ScrollViewer)window.FindName("AvailableTasksScroll");
            var planScroll = (System.Windows.Controls.ScrollViewer)window.FindName("PlanScroll");
            var search = (System.Windows.Controls.TextBox)window.FindName("TaskSearchBox");
            if (search.Visibility != System.Windows.Visibility.Collapsed) {
                throw new InvalidOperationException("搜索框默认不应占据任务区空间。");
            }
            foreach (var width in new[] { 1440, 920 }) {
                window.Width = width;
                window.Height = width == 920 ? 640 : 900;
                PumpOnboarding();
                if (shelf.ActualHeight > Math.Min(280, workspace.ActualHeight / 3) + 1
                    || catalogScroll.ScrollableHeight <= 0 || planScroll.ViewportHeight < 100
                    || catalogScroll.ExtentWidth > catalogScroll.ViewportWidth + 1) {
                    throw new InvalidOperationException("分类任务区必须限高、可滚动，并为执行计划保留空间。");
                }
                var planTop = planScroll.TranslatePoint(new System.Windows.Point(), workspace).Y;
                var planOffset = planScroll.VerticalOffset;
                catalogScroll.ScrollToBottom();
                PumpOnboarding();
                if (catalogScroll.VerticalOffset <= 0 || planScroll.VerticalOffset != planOffset
                    || Math.Abs(planScroll.TranslatePoint(new System.Windows.Point(), workspace).Y - planTop) > 1) {
                    throw new InvalidOperationException("滚动可用任务不得移动执行计划。");
                }
                catalogScroll.ScrollToTop();
                if (width == 920) {
                    planScroll.ScrollToBottom();
                    PumpOnboarding();
                    if (planScroll.VerticalOffset <= 0 || catalogScroll.VerticalOffset != 0) {
                        throw new InvalidOperationException("执行计划必须独立滚动。");
                    }
                    planScroll.ScrollToTop();
                }
            }
            var first = catalog.Children.OfType<System.Windows.Controls.Expander>().First();
            if (first.IsExpanded) {
                throw new InvalidOperationException("分类应遵循 default_expand=false。");
            }
            ClickOnboarding(window, "TaskSearchToggleButton");
            if (search.Visibility != System.Windows.Visibility.Visible) {
                throw new InvalidOperationException("点击搜索入口应展开输入框。");
            }
            search.Text = "RealTask";
            PumpOnboarding();
            var matches = catalog.Children.OfType<System.Windows.Controls.Expander>().ToArray();
            if (matches.Length != 2 || matches.Any(section => !section.IsExpanded)
                || ConfigurationDescendants(catalog).OfType<System.Windows.Controls.Button>()
                    .Any(button => button.Tag is ProjectTaskChoice && button.IsEnabled)) {
                throw new InvalidOperationException("搜索应展开所有匹配分类，多组任务共享已添加状态。");
            }
            search.Text = "no-such-task";
            PumpOnboarding();
            if (catalog.Children.Count != 1 || catalog.Children[0] is not System.Windows.Controls.TextBlock) {
                throw new InvalidOperationException("无搜索结果应显示空态。");
            }
            search.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window),
                0, System.Windows.Input.Key.Escape) {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            });
            PumpOnboarding();
            if (search.Visibility != System.Windows.Visibility.Collapsed || search.Text.Length != 0) {
                throw new InvalidOperationException("Esc 应清空并关闭任务搜索。");
            }
            first = catalog.Children.OfType<System.Windows.Controls.Expander>().First();
            if (first.IsExpanded) {
                throw new InvalidOperationException("清空搜索应恢复分类展开状态。");
            }
            first.IsExpanded = true;
            InvokeOnboarding(window, "RenderTaskPlan");
            PumpOnboarding();
            if (!catalog.Children.OfType<System.Windows.Controls.Expander>().First().IsExpanded) {
                throw new InvalidOperationException("刷新任务计划不应重置分类展开状态。");
            }
            catalog.Children.OfType<System.Windows.Controls.Expander>().First().IsExpanded = false;
            ClickOnboarding(window, "TaskSearchToggleButton");
            search.Text = "RealTask";
            ClickOnboarding(window, "TaskSearchToggleButton");
            if (search.Visibility != System.Windows.Visibility.Collapsed || search.Text.Length != 0) {
                throw new InvalidOperationException("关闭搜索按钮应清空并收起输入框。");
            }
            ClickOnboarding(window, "TaskShelfHeaderButton");
            ClickOnboarding(window, "TaskSearchToggleButton");
            if (shelf.Visibility != System.Windows.Visibility.Visible
                || search.Visibility != System.Windows.Visibility.Visible) {
                throw new InvalidOperationException("可用任务收起时，搜索入口应同时展开任务区。");
            }
            search.Text = "RealTask";
            ClickOnboarding(window, "TaskShelfHeaderButton");
            if (search.Text.Length != 0 || search.Visibility != System.Windows.Visibility.Collapsed) {
                throw new InvalidOperationException("收起任务区不得遗留隐藏的搜索筛选。");
            }
            ClickOnboarding(window, "TaskShelfHeaderButton");
            var screenshotDirectory = Environment.GetEnvironmentVariable("NARUTO_TASK_SCREENSHOTS");
            if (!string.IsNullOrEmpty(screenshotDirectory)) {
                Directory.CreateDirectory(screenshotDirectory);
                foreach (var width in new[] { 920, 1440 }) {
                    window.Width = width;
                    window.Height = width == 920 ? 640 : 900;
                    PumpOnboarding();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(screenshotDirectory, $"task-shelf-{width}.png"));
                    encoder.Save(stream);
                }
            }
        }, directory => {
            var path = Path.Combine(directory, "interface.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var groups = new JsonArray();
            for (var index = 0; index < 12; index++) {
                groups.Add(new JsonObject {
                    ["name"] = $"group{index}", ["label"] = $"任务分类 {index + 1}",
                    ["default_expand"] = index != 0
                });
            }
            root["group"] = groups;
            root["task"]![0]!["group"] = new JsonArray("group0", "group1");
            for (var index = 0; index < 36; index++) {
                root["task"]!.AsArray().Add(new JsonObject {
                    ["name"] = $"Task{index}", ["entry"] = $"Entry{index}",
                    ["label"] = index == 0 ? new string('长', 60) : $"日常任务 {index + 1}",
                    ["group"] = new JsonArray($"group{index % 12}")
                });
            }
            root["task"]!.AsArray().Add(new JsonObject {
                ["name"] = "Ungrouped", ["label"] = "未分组任务", ["entry"] = "Ungrouped",
                ["group"] = new JsonArray("unknown")
            });
            File.WriteAllText(path, root.ToJsonString());
        });
    }

    private static void VerifyTaskDescriptionMarkup()
    {
        var document = MarkdownDocument.Create(
            "# 标题\n\n普通段落 **粗体** *斜体* `code` [链接](https://example.com)  \n换行"
            + "\n\n- 第一项\n- 第二项\n\n3. 第三项\n4. 第四项", _ => { });
        var blocks = document.Blocks.ToArray();
        var heading = (System.Windows.Documents.Paragraph)blocks[0];
        var paragraph = (System.Windows.Documents.Paragraph)blocks[1];
        var inlines = paragraph.Inlines.ToArray();
        var unordered = (System.Windows.Documents.List)blocks[2];
        var ordered = (System.Windows.Documents.List)blocks[3];
        if (heading.FontSize <= document.FontSize || paragraph.LineHeight < document.FontSize
            || !inlines.Any(inline => inline.FontWeight == System.Windows.FontWeights.Bold)
            || !inlines.Any(inline => inline.FontStyle == System.Windows.FontStyles.Italic)
            || !inlines.OfType<System.Windows.Documents.Run>().Any(run => run.Text == "code")
            || !inlines.OfType<System.Windows.Documents.Hyperlink>().Any(link =>
                link.NavigateUri.AbsoluteUri == "https://example.com/")
            || !inlines.OfType<System.Windows.Documents.LineBreak>().Any()
            || unordered.MarkerStyle != System.Windows.TextMarkerStyle.Disc || unordered.ListItems.Count != 2
            || ordered.MarkerStyle != System.Windows.TextMarkerStyle.Decimal || ordered.StartIndex != 3) {
            throw new InvalidOperationException("任务说明 Markdown 标题、段落、强调、代码、链接或列表验证失败。");
        }
        const string legacy = "<span>旧说明</span><br>下一行";
        var legacyDocument = MarkdownDocument.Create(legacy, _ => { });
        var text = new System.Windows.Documents.TextRange(
            legacyDocument.ContentStart, legacyDocument.ContentEnd).Text.TrimEnd();
        if (text != legacy || MarkdownDocument.Create(string.Empty, _ => { }).Blocks.Count != 0) {
            throw new InvalidOperationException("旧 HTML 不应剥离标签或转换换行；空说明应为空文档。");
        }
    }

    private static void VerifyResponsiveOptionLayout()
    {
        if (ResponsiveWrapPanel.CalculateColumnCount(450, 220, 12, 3) != 1
            || ResponsiveWrapPanel.CalculateColumnCount(600, 220, 12, 3) != 2
            || ResponsiveWrapPanel.CalculateColumnCount(900, 220, 12, 3) != 3) {
            throw new InvalidOperationException("响应式 option 一至三列退化规则验证失败。");
        }
    }

    private static void VerifyRejectedInputEdit(ProjectPlanModule project, string inputName, string invalidValue)
    {
        try {
            project.SetInputValue("ServerRange", inputName, invalidValue);
            throw new InvalidOperationException($"PI input {inputName} 未拒绝非法显式值。");
        } catch (InvalidDataException) {
            // Expected: invalid edits are not persisted.
        }
    }

    private static void VerifyProtocolFrame()
    {
        using var stream = new MemoryStream();
        var requestId = Guid.NewGuid();
        var envelope = WireEnvelope.Request(
            ProtocolOperations.WorkerGetSnapshot, requestId, new { });
        var writer = new ProtocolConnection(stream);
        writer.WriteAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
        stream.Position = 0;
        var reader = new ProtocolConnection(stream);
        var decoded = reader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (decoded?.RequestId != requestId || decoded.Operation != ProtocolOperations.WorkerGetSnapshot) {
            throw new InvalidOperationException("Named Pipe frame round-trip 验证失败。");
        }
    }

    private static void VerifyPreviewProtocol()
    {
        var identity = new PreviewIdentity(Guid.NewGuid(), 1, Guid.NewGuid());
        var response = new PreviewResponse(identity, PreviewState.WaitingForWindow, 0, null);
        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);
        if (JsonSerializer.Deserialize<PreviewResponse>(json, ProtocolJson.Options) != response
            || json.Contains("png", StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException("Preview 订阅协议 round-trip 失败。");
        }
    }

    private static void VerifyWorkerLogSequenceTracker()
    {
        var firstInstance = Guid.NewGuid();
        var secondInstance = Guid.NewGuid();
        var tracker = new WorkerLogSequenceTracker();
        if (!tracker.BeginWorkerInstance(firstInstance)
            || tracker.Observe(1) != WorkerLogSequenceDisposition.Contiguous
            || tracker.Observe(3) != WorkerLogSequenceDisposition.Gap
            || tracker.LastContiguousSequence != 1
            || tracker.HighestObservedSequence != 3
            || tracker.Observe(2) != WorkerLogSequenceDisposition.Contiguous
            || tracker.Observe(3) != WorkerLogSequenceDisposition.Contiguous
            || tracker.Observe(3) != WorkerLogSequenceDisposition.Duplicate) {
            throw new InvalidOperationException("Worker 日志连续 sequence 跟踪验证失败。 ");
        }
        if (tracker.BeginWorkerInstance(firstInstance) || tracker.LastContiguousSequence != 3) {
            throw new InvalidOperationException("同一 Worker Instance 不应重置 Log Transport Cursor。 ");
        }
        if (!tracker.BeginWorkerInstance(secondInstance)
            || tracker.LastContiguousSequence != 0
            || tracker.HighestObservedSequence != 0) {
            throw new InvalidOperationException("新 Worker Instance 未重置 Log Transport Cursor。 ");
        }
        if (tracker.Observe(1) != WorkerLogSequenceDisposition.Contiguous
            || tracker.LastContiguousSequence != 1) {
            throw new InvalidOperationException("新 Worker Instance 重置后首条日志未被接受。 ");
        }
        if (tracker.Observe(5) != WorkerLogSequenceDisposition.Gap
            || tracker.LastContiguousSequence != 1
            || tracker.HighestObservedSequence != 5) {
            throw new InvalidOperationException("新 Worker Instance 的 gap 检测或 target 跟踪失败。 ");
        }

        tracker.ObserveTarget(8);
        tracker.SkipToFirstAvailable(6);
        if (tracker.LastContiguousSequence != 5 || tracker.Observe(6) != WorkerLogSequenceDisposition.Contiguous) {
            throw new InvalidOperationException("Worker 日志 eviction gap 恢复验证失败。 ");
        }
    }

    private static void VerifyRunLogRouting(AppLogger logger)
    {
        if (TypeDescriptor.GetProperties(typeof(MainWindow))[nameof(MainWindow.LogLines)] is null) {
            throw new InvalidOperationException("GUI 运行日志集合无法被 WPF Binding 发现。 ");
        }

        var timestampUtc = new DateTime(2026, 8, 24, 6, 30, 0, DateTimeKind.Utc);
        var userEntry = new WorkerLogEntry(
            1, timestampUtc, "INFO", ProtocolConstants.MaaNopRunLogSource, "用户日志",
            false, null, Guid.NewGuid(), Guid.NewGuid(), "Task");
        var diagnosticEntry = userEntry with { Sequence = 2, Source = "runtime.task" };
        var visibleEntries = new[] { userEntry, diagnosticEntry }
            .Select(MainWindow.CreateUserFacingRunLogEntry)
            .Where(entry => entry is not null)
            .Cast<LogEntry>()
            .ToArray();
        var expectedTimestamp = new DateTimeOffset(timestampUtc).ToLocalTime();
        if (visibleEntries.Length != 1
            || visibleEntries[0].Timestamp != expectedTimestamp
            || visibleEntries[0].Level != LogLevel.Info
            || visibleEntries[0].Message != userEntry.Message) {
            throw new InvalidOperationException("GUI 运行日志 source、timestamp 或 message 路由验证失败。 ");
        }

        MainWindow.WriteWorkerDiagnosticLog(logger, userEntry);
        MainWindow.WriteWorkerDiagnosticLog(logger, diagnosticEntry);
        logger.Info("GUI diagnostic only");
    }

    private static void VerifyEndedSessionControls(
        AppLogger logger, string testDirectory, string projectDirectory)
    {
        using var session = new ChildSessionManager(logger);
        var coordinator = new WorkerCoordinator(
            logger, Path.Combine(testDirectory, "home-controls"), "unused.exe",
            $"NarutoAutoGUI.Home.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
        var window = new MainWindow(
            logger, session, new ChildSessionProgramService(logger), coordinator,
            operation => operation(), () => Task.CompletedTask);
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void SetField(string name, object value)
        {
            typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        }
        void Refresh()
        {
            typeof(MainWindow).GetMethod("UpdateCommandAvailability", flags)!.Invoke(window, null);
        }
        var stop = (System.Windows.Controls.Button)window.FindName("StopTaskHeaderButton");
        var prepare = (System.Windows.Controls.Button)window.FindName("PrepareEnvironmentButton");
        try {
            var project = ProjectPlanModule.Open(projectDirectory, Path.Combine(testDirectory, "maanop-config.json"));
            SetField("_projectPlan", project);
            SetField("_projectConfigurationValid", true);
            SetField("_sessionSnapshot", new ChildSessionSnapshot(
                ChildSessionState.ConnectedHidden, 7, 1, "self-test"));
            var plan = project.CreateRunStartAttempt().Plan;
            var item = plan.Items[0];
            var active = new RunSnapshot(
                Guid.NewGuid(), "self-test", RunState.Running, DateTime.UtcNow, DateTime.UtcNow,
                null, null, item.PlanItemId, 0, plan,
                [new PlanItemSnapshot(item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry,
                    item.ResolvedOptions, item.PipelineOverride, PlanItemState.Running,
                    DateTime.UtcNow, null, null, null, null)], null, null);
            var available = new DependencyCheck(true, "self-test", null);
            var worker = new WorkerSnapshot(
                ProtocolConstants.SnapshotVersion, DateTime.UtcNow, 1, Guid.NewGuid(), Environment.ProcessId,
                7, "self-test", ProtocolConstants.ProtocolVersion, project.RuntimeProfileDigest, plan.Project,
                WorkerState.Ready, null,
                new DependencyStatus(DateTime.UtcNow, "self-test", "self-test",
                    available, available, available, available, available),
                RunState.Running, active, null, 0, 0);
            SetField("_workerSnapshot", new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, worker, ""));
            Refresh();
            if (stop.Visibility != System.Windows.Visibility.Visible || !stop.IsEnabled) {
                throw new InvalidOperationException("运行中应显示可点击的停止按钮。");
            }
            SetField("_workerSnapshot", new WorkerCoordinatorSnapshot(
                WorkerObservation.IpcDisconnected, false, worker, "暂时断线"));
            Refresh();
            if (stop.Visibility != System.Windows.Visibility.Visible || stop.IsEnabled) {
                throw new InvalidOperationException("暂时断线应保留运行状态并禁止停止。");
            }
            SetField("_sessionSnapshot", ChildSessionSnapshot.Empty);
            foreach (var state in new[] { RunState.Running, RunState.Starting, RunState.Stopping }) {
                var stale = worker with { RunState = state, ActiveRun = active with { State = state } };
                SetField("_workerSnapshot", new WorkerCoordinatorSnapshot(
                    WorkerObservation.ChildSessionEnded, false, stale, "Child Session 已结束"));
                Refresh();
                var ring = (System.Windows.UIElement)window.FindName("RuntimeHeaderProgressRing");
                var timer = (System.Windows.Threading.DispatcherTimer)
                    typeof(MainWindow).GetField("_elapsedTimer", flags)!.GetValue(window)!;
                var title = (System.Windows.Controls.TextBlock)window.FindName("HomeRunContextTitleText");
                if (stop.Visibility != System.Windows.Visibility.Collapsed
                    || prepare.Visibility != System.Windows.Visibility.Visible || !prepare.IsEnabled
                    || ring.Visibility != System.Windows.Visibility.Collapsed || timer.IsEnabled
                    || !title.Text.StartsWith("执行计划已配置", StringComparison.Ordinal)) {
                    throw new InvalidOperationException($"分身结束后仍显示 {state} 控件，未恢复准备运行环境入口。");
                }
                var retained = (WorkerCoordinatorSnapshot)
                    typeof(MainWindow).GetField("_workerSnapshot", flags)!.GetValue(window)!;
                if (!ReferenceEquals(retained.WorkerSnapshot, stale)) {
                    throw new InvalidOperationException("运行控件刷新不应修改最后已知快照。");
                }
            }
        } finally {
            window.AllowClose();
            window.Close();
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    private static void VerifyHomePresentation()
    {
        if (MainWindow.FormatElapsedTime(TimeSpan.Zero) != "00:00:00"
            || MainWindow.FormatElapsedTime(TimeSpan.FromSeconds(65)) != "00:01:05"
            || MainWindow.FormatElapsedTime(TimeSpan.FromSeconds(3661)) != "01:01:01") {
            throw new InvalidOperationException("Home elapsed time 格式化验证失败。 ");
        }

        var planItemId1 = Guid.NewGuid();
        var planItemId2 = Guid.NewGuid();
        var item1 = new PlanItemSnapshot(
            planItemId1, "TaskA", "任务 A", "EntryA",
            ProtocolJson.ToElement(new { }), ProtocolJson.ToElement(new[] { new { } }),
            PlanItemState.Succeeded, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1), null, null, null);
        var item2 = new PlanItemSnapshot(
            planItemId2, "TaskB", "任务 B", "EntryB",
            ProtocolJson.ToElement(new { }), ProtocolJson.ToElement(new[] { new { } }),
            PlanItemState.Running, DateTime.UtcNow.AddMinutes(-1), null, null, null, null);

        var runPlan = new RunPlan(
            1, DateTime.UtcNow, new ProjectProvenance("P", "1.0", 2, "d"), "r",
            ProtocolJson.ToElement(new { }), []);
        var run = new RunSnapshot(
            Guid.NewGuid(), "digest", RunState.Running, DateTime.UtcNow.AddMinutes(-2),
            DateTime.UtcNow.AddMinutes(-2), null, null, planItemId2, 1, runPlan,
            [item1, item2], null, null);

        var (curIdx, total, label) = MainWindow.GetCurrentRunProgress(run, null);
        if (curIdx != 2 || total != 2 || label != "任务 B") {
            throw new InvalidOperationException($"Home 当前任务 X/N 进度计算验证失败：({curIdx}/{total}, {label})。 ");
        }
    }

    private static void VerifyUnsupportedProjectConstraints(string testDirectory, string sourceProjectDirectory)
    {
        var sourceInterface = File.ReadAllText(Path.Combine(sourceProjectDirectory, "interface.json"));
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "unsupported-controller-option",
            "当前 MaaNOP GUI 不支持 $.controller[0].option",
            root => root["controller"]!.AsArray()[0]!.AsObject()["option"] =
                new JsonArray("ServerRange"));
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "unsupported-resource-option",
            "当前 MaaNOP GUI 不支持 $.resource[0].option",
            root => root["resource"]!.AsArray()[0]!.AsObject()["option"] =
                new JsonArray("ServerRange"));
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "unsupported-resource-controller",
            "$.resource[0].controller",
            root => root["resource"]!.AsArray()[0]!.AsObject()["controller"] =
                new JsonArray("Win32"));
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "unsupported-task-controller",
            "$.task[0].controller",
            root => root["task"]!.AsArray()[0]!.AsObject()["controller"] =
                new JsonArray("Win32"));
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "unsupported-option-resource",
            "$.option.ServerRange.resource",
            root => root["option"]!.AsObject()["ServerRange"]!.AsObject()["resource"] =
                new JsonArray("Default"));
    }

    private static void VerifyInvalidProjectInterfaces(string testDirectory, string sourceProjectDirectory)
    {
        var emptyProjectDirectory = Path.Combine(testDirectory, "missing-interface");
        Directory.CreateDirectory(emptyProjectDirectory);
        try {
            _ = ProjectPlanModule.Open(
                emptyProjectDirectory, Path.Combine(emptyProjectDirectory, "maanop-config.json"));
            throw new InvalidOperationException("PI 未拒绝缺失 interface.json 的项目目录。");
        } catch (FileNotFoundException exception) when (
            exception.Message.Contains("安装目录缺少 interface.json", StringComparison.Ordinal)) {
            // Expected: clear installation-oriented error message.
        }

        var sourceInterface = File.ReadAllText(Path.Combine(sourceProjectDirectory, "interface.json"));
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-class-regex",
            "$.controller[0].win32.class_regex 不是合法正则表达式",
            root => root["controller"]!.AsArray()[0]!.AsObject()["win32"]!
                .AsObject()["class_regex"] = "(");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-window-regex",
            "$.controller[0].win32.window_regex 不是合法正则表达式",
            root => root["controller"]!.AsArray()[0]!.AsObject()["win32"]!
                .AsObject()["window_regex"] = "[");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-screencap-method",
            "$.controller[0].win32.screencap 不支持值 UnknownScreencap",
            root => root["controller"]!.AsArray()[0]!.AsObject()["win32"]!
                .AsObject()["screencap"] = "UnknownScreencap");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-mouse-method",
            "$.controller[0].win32.mouse 不支持值 UnknownMouse",
            root => root["controller"]!.AsArray()[0]!.AsObject()["win32"]!
                .AsObject()["mouse"] = "UnknownMouse");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-keyboard-method",
            "$.controller[0].win32.keyboard 不支持值 UnknownKeyboard",
            root => root["controller"]!.AsArray()[0]!.AsObject()["win32"]!
                .AsObject()["keyboard"] = "UnknownKeyboard");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-input-cases",
            "input option $.option.ServerRange 不能声明 cases",
            root => root["option"]!.AsObject()["ServerRange"]!.AsObject()["cases"] =
                new JsonArray(new JsonObject { ["name"] = "Unexpected" }));
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-select-pipeline-override",
            "select option $.option.Mode 不能声明 pipeline_override",
            root => root["option"]!.AsObject()["Mode"]!.AsObject()["pipeline_override"] =
                new JsonObject { ["Unexpected"] = new JsonObject() });
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-default-case",
            "$.option.Mode.default_case",
            root => root["option"]!.AsObject()["Mode"]!.AsObject()["default_case"] =
                "Missing");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-input-regex",
            "$.option.ServerRange.inputs[0].verify",
            root => root["option"]!.AsObject()["ServerRange"]!.AsObject()["inputs"]!
                .AsArray()[0]!.AsObject()["verify"] = "(");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "invalid-input-default-type",
            "$.option.ServerRange.inputs[0].default 不是合法 int",
            root => root["option"]!.AsObject()["ServerRange"]!.AsObject()["inputs"]!
                .AsArray()[0]!.AsObject()["pipeline_type"] = "int");
        VerifyRejectedProjectInterface(
            testDirectory, sourceInterface,
            "inactive-option-cycle",
            "option 递归引用形成循环：Mode -> Mode",
            root =>
            {
                var cases = root["option"]!.AsObject()["Mode"]!.AsObject()["cases"]!.AsArray();
                var minimal = cases
                    .Select(item => item!.AsObject())
                    .Single(item => item["name"]!.GetValue<string>() == "Minimal");
                minimal["option"] = new JsonArray("Mode");
            });
    }

    private static void VerifyRejectedProjectInterface(
        string testDirectory, string sourceInterface, string fixtureName,
        string expectedError, Action<JsonObject> mutate)
    {
        var projectDirectory = Path.Combine(testDirectory, fixtureName);
        Directory.CreateDirectory(projectDirectory);
        var root = JsonNode.Parse(sourceInterface)?.AsObject()
                   ?? throw new InvalidOperationException("PI fail-closed fixture 解析失败。");
        mutate(root);
        File.WriteAllText(Path.Combine(projectDirectory, "interface.json"), root.ToJsonString());

        try {
            _ = ProjectPlanModule.Open(projectDirectory, Path.Combine(projectDirectory, "maanop-config.json"));
            throw new InvalidOperationException($"PI 未拒绝非法定义：{fixtureName}。");
        } catch (InvalidDataException exception)
              when (exception.Message.Contains(expectedError, StringComparison.Ordinal)) {
            // Expected: unsupported or invalid PI semantics fail closed with useful context.
        }
    }

    private static string CreateProjectFixture(string testDirectory)
    {
        var projectDirectory = Path.Combine(testDirectory, "project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "agent"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "resource"));
        File.WriteAllText(
            Path.Combine(projectDirectory, "interface.json"),
            """
            {
              "interface_version": 2,
              "name": "SelfTestProject",
              "version": "1.0.0",
              "github": "https://github.com/example/fixture",
              "controller": [{
                "name": "Win32",
                "type": "Win32",
                "option": [],
                "win32": {
                  "class_regex": ".*",
                  "window_regex": "SelfTestWindow",
                  "screencap": "PrintWindow",
                  "mouse": "Seize",
                  "keyboard": "Seize"
                }
              }],
              "resource": [{
                "name": "Default",
                "path": ["./resource"],
                "option": []
              }],
              "agent": {"child_exec": "python", "child_args": ["./agent/main.py"]},
              "global_option": ["ServerRange"],
              "task": [{
                "name": "RealTask",
                "label": "Real task",
                "description": "A long task description retained for multiline display on the Tasks page.",
                "entry": "SelfTestEntry",
                "option": ["Mode"],
                "pipeline_override": {
                  "SelfTestEntry": {"enabled": true},
                  "ScopeOrder": {"task": true}
                }
              }],
              "option": {
                "ServerRange": {
                  "type": "input",
                  "label": "Server range",
                  "description": "A long option description that wraps beside its editor without horizontal scrolling.",
                  "inputs": [
                    {
                      "name": "server_range",
                      "label": "Server",
                      "default": "978-1012",
                      "pipeline_type": "string",
                      "verify": "^(?:\\d+(?:-\\d+)?)(?:,\\d+(?:-\\d+)?)*$",
                      "pattern_msg": "Use ranges such as 978 or 978-1012"
                    },
                    {
                      "name": "retry_count",
                      "label": "Retry count",
                      "default": "3",
                      "pipeline_type": "int"
                    },
                    {
                      "name": "enabled",
                      "label": "Enabled",
                      "default": "true",
                      "pipeline_type": "bool"
                    }
                  ],
                  "pipeline_override": {
                    "ParseServer": {
                      "recognition": {
                        "type": "Custom",
                        "param": {"custom_recognition_param": "{server_range}"}
                      }
                    },
                    "TypedValues": {
                      "retry_count": "{retry_count}",
                      "enabled": "{enabled}",
                      "summary": "{server_range}:{retry_count}:{enabled}"
                    },
                    "ScopeOrder": {"global": true}
                  }
                },
                "Mode": {
                  "type": "select",
                  "description": "Choose how the task should run.",
                  "default_case": "Default",
                  "cases": [
                    {
                      "name": "Default",
                      "label": "Default mode",
                      "option": ["Nested"],
                      "pipeline_override": {
                        "SelfTestEntry": {"mode": "default"},
                        "ScopeOrder": {"task_option": true}
                      }
                    },
                    {
                      "name": "Minimal",
                      "label": "Minimal mode",
                      "pipeline_override": {"SelfTestEntry": {"mode": "minimal"}}
                    }
                  ]
                },
                "Nested": {
                  "type": "switch",
                  "default_case": "On",
                  "cases": [
                    {
                      "name": "On",
                      "pipeline_override": {
                        "SelfTestEntry": {"nested": true},
                        "ScopeOrder": {"nested": true}
                      }
                    },
                    {
                      "name": "Off",
                      "pipeline_override": {
                        "SelfTestEntry": {"nested": false},
                        "ScopeOrder": {"nested": false}
                      }
                    }
                  ]
                }
              }
            }
            """);
        return Path.GetFullPath(projectDirectory);
    }
}
