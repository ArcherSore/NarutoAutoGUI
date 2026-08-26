using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static class SelfTestRunner
{
    internal static int Run()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"NarutoAutoGUI-self-test-{Guid.NewGuid():N}");

        try {
            Directory.CreateDirectory(testDirectory);
            var logDirectory = Path.Combine(testDirectory, "logs");
            using var logger = new AppLogger(logDirectory);
            var projectDirectory = CreateProjectFixture(testDirectory);
            VerifySettings(logger, testDirectory, projectDirectory);
            VerifyProjectPlan(testDirectory, projectDirectory);
            VerifyUnsupportedProjectConstraints(testDirectory, projectDirectory);
            VerifyInvalidProjectInterfaces(testDirectory, projectDirectory);
            VerifyProtocolFrame();
            VerifyPreviewProtocol();
            VerifyWorkerLogSequenceTracker();
            VerifyRunLogRouting(logger);
            Task.Run(() => WorkerCoordinatorSelfTest.RunAsync(
                logger, testDirectory, projectDirectory,
                Path.Combine(testDirectory, "maanop-config.json"))).GetAwaiter().GetResult();

            logger.Debug("self-test-debug");
            logger.Info("self-test-info");
            logger.Dispose();
            var logText = string.Join(Environment.NewLine, Directory.EnumerateFiles(logDirectory, "*.log").Select(File.ReadAllText));
            if (!logText.Contains("[DEBUG] self-test-debug", StringComparison.Ordinal)
                || !logText.Contains("[INFO] self-test-info", StringComparison.Ordinal)
                || !logText.Contains("[maanop.run] 用户日志", StringComparison.Ordinal)
                || !logText.Contains("[runtime.task] 用户日志", StringComparison.Ordinal)
                || !logText.Contains("GUI diagnostic only", StringComparison.Ordinal)) {
                throw new InvalidOperationException("DEBUG+ 文件日志验证失败。");
            }

            Console.WriteLine(
                "SELF-TEST PASS: settings v2 + legacy migration; PI default/explicit resolver; "
                + "ordered pipeline override; nested dormant intent; "
                + "Win32 PI validation; unsupported PI scope/constraint fail-closed; "
                + "PI structure/default/graph validation; typed input validation; "
                + "MaaNOP Config v1; RunPlan digest; IPC framing; preview schema; "
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

    private static void VerifySettings(AppLogger logger, string testDirectory, string projectDirectory)
    {
        var settingsPath = Path.Combine(testDirectory, "settings.json");
        var store = new AppSettingsStore(logger, settingsPath);
        var legacyDirectory = Path.Combine(testDirectory, "legacy-maanop");
        var legacyAssets = Path.Combine(legacyDirectory, "assets");
        Directory.CreateDirectory(legacyAssets);
        File.WriteAllText(Path.Combine(legacyAssets, "interface.json"), "{}");
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(new
            {
                GameExecutablePath = Path.Combine(testDirectory, "QQGameLauncher.exe"),
                MaaNopExecutablePath = Path.Combine(legacyDirectory, "MFAAvalonia.exe")
            }));
        var migrated = store.Load();
        if (migrated.GameExecutablePath != AppSettings.DefaultGameExecutablePath
            || migrated.GameArguments != AppSettings.DefaultGameArguments
            || migrated.MaaNopProjectDirectory != Path.GetFullPath(legacyAssets)) {
            throw new InvalidOperationException("旧版 Application Settings 内存迁移验证失败。");
        }

        var expected = new AppSettings {
            GameExecutablePath = @"C:\Test\Game.exe",
            GameArguments = "--test-game",
            MaaNopProjectDirectory = projectDirectory
        };
        store.Save(expected);
        var savedJson = File.ReadAllText(settingsPath);
        if (savedJson.Contains("MaaNopExecutablePath", StringComparison.Ordinal)
            || savedJson.Contains("GameWorkingDirectory", StringComparison.Ordinal)
            || !savedJson.Contains("\"SchemaVersion\": 2", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Application Settings SchemaVersion 2 序列化验证失败。");
        }

        var actual = store.Load();
        if (actual.GameExecutablePath != expected.GameExecutablePath || actual.GameArguments != expected.GameArguments
            || actual.MaaNopProjectDirectory != expected.MaaNopProjectDirectory) {
            throw new InvalidOperationException("Application Settings 持久化往返验证失败。");
        }
    }

    private static void VerifyProjectPlan(string testDirectory, string projectDirectory)
    {
        var configPath = Path.Combine(testDirectory, "maanop-config.json");
        var project = ProjectPlanModule.Open(projectDirectory, configPath);
        if (project.Tasks.Count != 1 || project.Tasks[0].Name != "RealTask" || project.Tasks[0].Label != "Real task") {
            throw new InvalidOperationException("PI task catalog 验证失败。");
        }

        project.SelectTask("RealTask");
        using (var configDocument = JsonDocument.Parse(File.ReadAllBytes(configPath))) {
            var root = configDocument.RootElement;
            if (root.GetProperty("SchemaVersion").GetInt32() != MaaNopConfig.CurrentSchemaVersion
                || root.GetProperty("SelectedTasks")[0].GetString() != "RealTask"
                || root.GetProperty("ExplicitOptions").EnumerateObject().Any()) {
                throw new InvalidOperationException(
                    $"MaaNOP Config SchemaVersion {MaaNopConfig.CurrentSchemaVersion} 验证失败。");
            }
        }

        var defaultConfiguration = project.GetConfiguration();
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
            var explicitOptions = configDocument.RootElement.GetProperty("ExplicitOptions");
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
        var dormantConfiguration = project.GetConfiguration();
        if (dormantConfiguration.TaskOptions.Single().ActiveChildren.Count != 0) {
            throw new InvalidOperationException("PI nested option active graph 验证失败。");
        }
        using (var configDocument = JsonDocument.Parse(File.ReadAllBytes(configPath))) {
            if (configDocument.RootElement.GetProperty("ExplicitOptions")
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
        var retainedInputs = project.GetConfiguration().GlobalOptions.Single().Inputs
            .ToDictionary(input => input.Name, input => input.Value, StringComparer.Ordinal);
        if (retainedInputs["server_range"] != "978"
            || retainedInputs["retry_count"] != "3"
            || retainedInputs["enabled"] != "true") {
            throw new InvalidOperationException("非法显式值不应覆盖最后一次合法配置。");
        }

        project.FollowProjectDefault("ServerRange");
        project.FollowProjectDefault("Nested");
        project.FollowProjectDefault("Mode");
        var resetAttempt = project.CreateRunStartAttempt();
        if (resetAttempt.Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                .GetProperty("server_range").GetString() != "978-1012"
            || resetAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Mode").GetString() != "Default"
            || resetAttempt.Plan.Items[0].ResolvedOptions.GetProperty("Nested").GetString() != "On") {
            throw new InvalidOperationException("PI option 跟随项目默认验证失败。");
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
        var response = new PreviewGetLatestResponse(
            "frame", Guid.NewGuid(), Guid.NewGuid(), 3,
            new DateTime(2026, 8, 26, 8, 30, 0, DateTimeKind.Utc), 4, 3, "image/png",
            [1, 2, 3], null);
        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);
        if (!json.Contains("\"sampledAtUtc\"", StringComparison.Ordinal)
            || json.Contains("capturedAtUtc", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Preview timestamp 字段名不是 sampledAtUtc。 ");
        }
        var decoded = JsonSerializer.Deserialize<PreviewGetLatestResponse>(json, ProtocolJson.Options);
        if (decoded is null
            || decoded with { PngBytes = response.PngBytes } != response
            || !decoded.PngBytes!.SequenceEqual(response.PngBytes!)) {
            throw new InvalidOperationException("Preview JSON/base64 round-trip 验证失败。 ");
        }

        var staleFieldJson = json.Replace("\"sampledAtUtc\"", "\"capturedAtUtc\"", StringComparison.Ordinal);
        try {
            _ = JsonSerializer.Deserialize<PreviewGetLatestResponse>(staleFieldJson, ProtocolJson.Options);
            throw new InvalidOperationException("Preview schema 未拒绝旧 capturedAtUtc 字段。 ");
        } catch (JsonException) {
            // Expected: GUI and Worker ship together and use one strict schema.
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
