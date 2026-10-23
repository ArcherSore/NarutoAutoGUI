using System.Text.Json;
using System.Text.Json.Nodes;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyPretaskModel(string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "pretask-model");
        Directory.CreateDirectory(directory);
        VerifyPretaskDefinitions(directory);
        VerifyPretaskOrdering(directory);
        VerifyPretaskArguments(directory);
        VerifyPretaskInputDefaults(directory);
        VerifyPretaskSharedInputs(directory);
        VerifyPretaskConfigurationMigration(directory);
        VerifyInvalidPretaskDefinitions(directory);
        VerifyPretaskExecutablePaths(directory);
    }

    private static void VerifyPretaskDefinitions(string testDirectory)
    {
        var directory = CreatePretaskFixture(Path.Combine(testDirectory, "single"), """
            {"exec":"prepare-fixture","label":"准备测试环境"}
            """);
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        Require(project.Pretasks.Single() is {
            Name: "prepare-fixture", Label: "准备测试环境", Kind: PlanItemKind.Pretask
        } && project.SelectedPretaskNames.Count == 0,
            "单对象 pretask 必须支持缺省 name，并且首次配置不能默认选择前置任务。");
        Require(project.AddPretask("prepare-fixture") && !project.AddPretask("prepare-fixture"),
            "同一前置任务只能添加一次。");
        project.RemoveTask("RealTask");
        var attempt = project.CreateRunStartAttempt();
        var item = attempt.Plan.Items.Single();
        Require(item.Kind == PlanItemKind.Pretask && item.Entry == string.Empty
            && item.Program is { Executable: "prepare-fixture", Arguments.Count: 0 }
            && item.PipelineOverride.ValueKind == JsonValueKind.Array && item.PipelineOverride.GetArrayLength() == 0
            && project.SelectedItems.Single().Kind == PlanItemKind.Pretask,
            "仅前置任务的计划必须保留程序类型及空参数，不能生成 MaaTask 或 Pipeline Override。");
        Require(project.RemovePretask("prepare-fixture") && !project.RemovePretask("prepare-fixture")
            && Throws<InvalidOperationException>(() => project.CreateRunStartAttempt()),
            "移除最后一个前置任务后应为空计划，不能继续开始。");

        directory = CreatePretaskFixture(Path.Combine(testDirectory, "empty-label"), """
            {"name":"EmptyLabel","label":"","exec":"prepare-fixture"}
            """);
        project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        project.AddPretask("EmptyLabel");
        Require(project.CreateRunStartAttempt().Plan.Items[0].TaskLabel == "EmptyLabel",
            "空展示标签必须回退到前置任务名称，不能生成 Worker 拒绝的计划。");

        directory = CreatePretaskFixture(Path.Combine(testDirectory, "shared-name"), """
            {"name":"RealTask","exec":"prepare-fixture","option":["StartupMode"]}
            """);
        project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        project.AddPretask("RealTask");
        Require(project.SelectedItems.Select(choice => choice.Kind)
                .SequenceEqual([PlanItemKind.Pretask, PlanItemKind.MaaTask])
            && project.GetConfiguration("RealTask", PlanItemKind.Pretask).TaskOptions.Single().Name == "StartupMode"
            && project.GetConfiguration("RealTask").TaskOptions.Single().Name == "Mode",
            "前置任务与普通任务的同名标识必须由 Kind 区分，编辑器不能串用选项。");
    }

    private static void VerifyPretaskOrdering(string testDirectory)
    {
        var directory = CreatePretaskFixture(Path.Combine(testDirectory, "ordering"), """
            [
              {"name":"First","exec":"first-fixture","controller":["Win32"],"resource":["Default"],"args":[]},
              {"name":"Second","exec":"second-fixture"},
              {"name":"OtherController","exec":"other-controller","controller":["Other"]},
              {"name":"OtherResource","exec":"other-resource","resource":["Other"]}
            ]
            """);
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        Require(project.Pretasks.Select(choice => choice.Name).SequenceEqual(["First", "Second"])
            && Throws<InvalidOperationException>(() => project.AddPretask("OtherController"))
            && Throws<InvalidOperationException>(() => project.AddPretask("OtherResource"))
            && Throws<ArgumentException>(() => project.AddPretask("Missing")),
            "前置任务目录和添加入口必须过滤不适用的 controller/resource 并拒绝未知名称。");
        project.AddPretask("Second");
        project.AddPretask("First");
        var attempt = project.CreateRunStartAttempt();
        Require(project.SelectedItems.Select(choice => choice.Name).SequenceEqual(["First", "Second", "RealTask"])
            && attempt.Plan.Items.Select(item => item.TaskName).SequenceEqual(["First", "Second", "RealTask"])
            && attempt.Plan.Items.Take(2).All(item => item.Kind == PlanItemKind.Pretask
                && item.Program!.Arguments.Count == 0)
            && attempt.Plan.Items[2].Kind == PlanItemKind.MaaTask,
            "添加顺序不能改变 PI 前置任务顺序，且所有前置任务必须位于普通任务之前。");

        var baseline = attempt.Plan;
        var first = baseline.Items[0];
        foreach (var changed in new[] {
            first with { Kind = PlanItemKind.MaaTask },
            first with { Program = first.Program! with { Executable = "changed-fixture" } },
            first with { Program = first.Program! with { Arguments = ["changed"] } }
        }) {
            var changedPlan = baseline with { Items = [changed, .. baseline.Items.Skip(1)] };
            Require(CanonicalDigest.ComputePlanDigestV2(changedPlan) != attempt.PlanDigest,
                "Plan Digest 必须覆盖前置任务的 Kind、程序路径和参数。");
        }
        var restored = ProtocolJson.Deserialize<RunPlan>(ProtocolJson.ToElement(baseline));
        Require(restored.Items[0].Kind == PlanItemKind.Pretask
            && restored.Items[0].Program?.Executable == "first-fixture"
            && CanonicalDigest.ComputePlanDigestV2(restored) == attempt.PlanDigest,
            "协议序列化往返必须保留前置任务类型、程序和 Plan Digest。");
    }

    private static void VerifyPretaskArguments(string testDirectory)
    {
        var directory = CreatePretaskFixture(Path.Combine(testDirectory, "arguments"), """
            {"name":"Prepare","exec":"./python/python.exe","args":["./agent/prepare.py","","固定 参数"],
             "option":["StartupMode","StartupGate"]}
            """);
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        var originalOverrides = project.CreateRunStartAttempt().Plan.Items.Single().PipelineOverride.GetRawText();
        project.AddPretask("Prepare");
        const string caption = "测试 \"引号\"、路径 C:\\临时\\目录\n第二行";
        project.SetInputValue("LaunchInput", "caption", caption);
        project.SetInputValue("LaunchInput", "attempts", "003");
        var attempt = project.CreateRunStartAttempt();
        var pretask = attempt.Plan.Items[0];
        var program = pretask.Program!;
        Require(Path.GetFullPath(program.Executable)
                == Path.GetFullPath(Path.Combine(directory, "python", "python.exe"))
            && program.Arguments.Take(3).SequenceEqual(["./agent/prepare.py", "", "固定 参数"])
            && program.Arguments.Count == 4 && !program.Arguments[^1].Contains('\n')
            && !program.Arguments[^1].Contains('\r'),
            "相对程序必须按项目根目录解析，固定参数和空参数应原样保留，选项 JSON 只追加一个紧凑参数。");
        using (var document = JsonDocument.Parse(program.Arguments[^1])) {
            var values = document.RootElement;
            var inputs = values.GetProperty("LaunchInput");
            Require(values.GetProperty("StartupMode").GetString() == "Auto"
                && values.GetProperty("StartupGate").GetString() == "On"
                && inputs.GetProperty("caption").GetString() == caption
                && inputs.GetProperty("attempts").ValueKind == JsonValueKind.String
                && inputs.GetProperty("attempts").GetString() == "003"
                && inputs.GetProperty("enabled").GetString() == "true"
                && values.GetRawText() == pretask.ResolvedOptions.GetRawText(),
                "前置参数应包含激活子选项，input 始终为字符串，中文、引号及换行必须无损传递。");
        }
        Require(attempt.Plan.Items[1].PipelineOverride.GetRawText() == originalOverrides
            && !attempt.Plan.Items[1].PipelineOverride.GetRawText().Contains("PretaskOnly", StringComparison.Ordinal)
            && pretask.PipelineOverride.GetArrayLength() == 0,
            "前置选项的 override 不能污染普通任务，也不能作为前置任务的 Pipeline Override。");
        var editor = project.GetConfiguration("Prepare", PlanItemKind.Pretask);
        Require(editor.GlobalOptions.Count == 0
            && editor.TaskOptions.Single(option => option.Name == "StartupMode")
                .ActiveChildren.Single().Inputs.Single(input => input.Name == "caption").Value == caption,
            "前置任务编辑器应只展示其选项，并恢复激活子选项的显式输入。");

        project.SetSelectedCase("StartupMode", "Manual");
        pretask = project.CreateRunStartAttempt().Plan.Items[0];
        Require(!pretask.ResolvedOptions.TryGetProperty("LaunchInput", out _)
            && project.Configurations.Single().ExplicitOptions.ContainsKey("LaunchInput"),
            "休眠子选项不应传给程序，但其显式意图必须保留。");
        project.SetSelectedCase("StartupMode", "Auto");
        Require(project.CreateRunStartAttempt().Plan.Items[0].ResolvedOptions.GetProperty("LaunchInput")
                .GetProperty("caption").GetString() == caption,
            "重新激活子选项后应恢复原输入。");
        project.RemoveTask("RealTask");
        project.SetInputValue("LaunchInput", "attempts", "非数字");
        Require(project.CheckInputValue("LaunchInput", "attempts", "非数字") is null
            && project.CreateRunStartAttempt().Plan.Items.Single().ResolvedOptions.GetProperty("LaunchInput")
                .GetProperty("attempts").GetString() == "非数字",
            "前置 input 不参与 pipeline_type 数值转换，不能套用普通任务的类型约束。");
    }

    private static void VerifyPretaskInputDefaults(string testDirectory)
    {
        const string declaration = """
            {"name":"Prepare","exec":"prepare-fixture","option":["StartupMode"]}
            """;
        foreach (var (name, attempts, enabled, mode) in new[] {
            ("empty", "", "", "Auto"), ("text", "按需", "未启用", "Manual")
        }) {
            var directory = CreatePretaskFixture(Path.Combine(testDirectory, $"defaults-{name}"), declaration);
            var path = Path.Combine(directory, "interface.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var inputs = root["option"]!["LaunchInput"]!["inputs"]!.AsArray();
            inputs.Single(input => input!["name"]!.GetValue<string>() == "attempts")!["default"] = attempts;
            inputs.Single(input => input!["name"]!.GetValue<string>() == "enabled")!["default"] = enabled;
            root["option"]!["StartupMode"]!["default_case"] = mode;
            File.WriteAllText(path, root.ToJsonString());
            var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
            project.AddPretask("Prepare");
            if (mode == "Manual") {
                Require(!project.CreateRunStartAttempt().Plan.Items[0].ResolvedOptions
                    .TryGetProperty("LaunchInput", out _), "未激活的前置子选项不应生成参数。");
                project.SetSelectedCase("StartupMode", "Auto");
            }
            var item = project.CreateRunStartAttempt().Plan.Items[0];
            var defaults = item.ResolvedOptions.GetProperty("LaunchInput");
            using var arguments = JsonDocument.Parse(item.Program!.Arguments.Single());
            Require(defaults.GetProperty("attempts").ValueKind == JsonValueKind.String
                && defaults.GetProperty("attempts").GetString() == attempts
                && defaults.GetProperty("enabled").ValueKind == JsonValueKind.String
                && defaults.GetProperty("enabled").GetString() == enabled
                && arguments.RootElement.GetProperty("LaunchInput").GetProperty("attempts").GetString() == attempts
                && arguments.RootElement.GetProperty("LaunchInput").GetProperty("enabled").GetString() == enabled,
                "仅供前置任务使用的 int/bool input 默认值应按字符串加载，包含空值和不可转换文本。");
        }

        var sharedDirectory = CreatePretaskFixture(Path.Combine(testDirectory, "shared-defaults"), declaration);
        var sharedRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(sharedDirectory, "interface.json")))!.AsObject();
        foreach (var inputName in new[] { "attempts", "enabled" }) {
            var root = sharedRoot.DeepClone().AsObject();
            root["option"]!["LaunchInput"]!["inputs"]!.AsArray()
                .Single(input => input!["name"]!.GetValue<string>() == inputName)!["default"] = "";
            var source = root.ToJsonString();
            foreach (var scope in new[] { "task", "global" }) {
                VerifyRejectedProjectInterface(testDirectory, source, $"shared-{scope}-{inputName}", "LaunchInput",
                    definition => {
                        var options = scope == "task"
                            ? definition["task"]![0]!["option"]!.AsArray()
                            : definition["global_option"]!.AsArray();
                        options.Add("LaunchInput");
                    });
            }
            VerifyRejectedProjectInterface(testDirectory, source, $"shared-dormant-{inputName}", "LaunchInput",
                definition => definition["option"]!["Mode"]!["cases"]![1]!["option"] = new JsonArray("LaunchInput"));
            VerifyRejectedProjectInterface(testDirectory, source, $"unused-default-{inputName}", "LaunchInput",
                definition => definition["pretask"] = new JsonArray());
        }
    }

    // An input shared with a task is typed exactly where saving converts it: in options a selected task resolves
    // through active cases. Otherwise the field check passes a value the save then rejects as a whole-config error.
    private static void VerifyPretaskSharedInputs(string testDirectory)
    {
        var directory = CreatePretaskFixture(Path.Combine(testDirectory, "shared-inputs"), """
            {"name":"Prepare","exec":"prepare-fixture","option":["ServerRange","SharedCount"]}
            """);
        var path = Path.Combine(directory, "interface.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["option"]!["SharedCount"] = JsonNode.Parse("""
            {"type":"input","inputs":[{"name":"count","default":"2","pipeline_type":"int"}],
             "pipeline_override":{"SharedEntry":{"count":"{count}"}}}
            """);
        root["option"]!["Mode"]!["cases"]![1]!["option"] = new JsonArray("SharedCount");
        File.WriteAllText(path, root.ToJsonString());
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        project.AddPretask("Prepare");
        void RequireConsistent(string optionName, string inputName, bool typed, string scenario)
        {
            var problem = project.CheckInputValue(optionName, inputName, "多次");
            var saved = true;
            try {
                project.SetInputValue(optionName, inputName, "多次");
            } catch (InvalidDataException) {
                saved = false;
            }
            Require((problem is not null) == typed && saved != typed,
                $"{scenario}：单字段检查必须与保存时的 pipeline_type 转换一致。");
        }

        RequireConsistent("ServerRange", "retry_count", typed: true, "已选普通任务解析的全局 option");
        RequireConsistent("SharedCount", "count", typed: false, "只在普通任务休眠分支中的 option");
        project.SetInputValue("SharedCount", "count", "5");
        project.SetSelectedCase("Mode", "Minimal");
        RequireConsistent("SharedCount", "count", typed: true, "普通任务激活分支中的 option");
        project.RemoveTask("RealTask");
        RequireConsistent("ServerRange", "retry_count", typed: false, "只剩前置任务时的全局 option");
        RequireConsistent("SharedCount", "count", typed: false, "只剩前置任务时的共享 option");
        var values = project.CreateRunStartAttempt().Plan.Items.Single().ResolvedOptions;
        Require(values.GetProperty("ServerRange").GetProperty("retry_count").GetString() == "多次"
            && values.GetProperty("SharedCount").GetProperty("count").GetString() == "多次",
            "只供前置任务使用的共享 input 应按原文传给程序。");
    }

    private static void VerifyPretaskConfigurationMigration(string testDirectory)
    {
        var directory = CreatePretaskFixture(Path.Combine(testDirectory, "migration"), """
            [
              {"name":"Prepare","exec":"prepare-fixture","option":["StartupMode"]},
              {"name":"Finish","exec":"finish-fixture"}
            ]
            """);
        var path = Path.Combine(directory, "config.json");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var intent = new Dictionary<string, JsonElement> {
            ["Mode"] = JsonSerializer.SerializeToElement(new { SelectedCase = "Minimal" }),
            ["ServerRange"] = JsonSerializer.SerializeToElement(new { Inputs = new { server_range = "978" } })
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            SchemaVersion = 2, ActiveConfigurationId = first,
            Configurations = new[] {
                new { Id = first, Name = "First", SelectedTasks = new[] { "RealTask" }, ExplicitOptions = intent },
                new { Id = second, Name = "Second", SelectedTasks = Array.Empty<string>(), ExplicitOptions = intent }
            }
        }));
        var project = ProjectPlanModule.Open(directory, path);
        using (var document = JsonDocument.Parse(File.ReadAllBytes(path))) {
            Require(document.RootElement.GetProperty("SchemaVersion").GetInt32() == 3
                && project.ActiveConfigurationId == first && project.SelectedTaskNames.SequenceEqual(["RealTask"])
                && project.Configurations.All(config => config.SelectedPretasks.Count == 0)
                && project.Configurations.All(config => config.ExplicitOptions["Mode"]
                    .GetProperty("SelectedCase").GetString() == "Minimal")
                && project.CreateRunStartAttempt().Plan.ResolvedGlobalOptions.GetProperty("ServerRange")
                    .GetProperty("server_range").GetString() == "978",
                "V2 配置迁移必须保留配置身份、激活项、任务和显式意图，并默认不选前置任务。");
        }
        project.AddPretask("Prepare");
        project.SetInputValue("LaunchInput", "caption", "first");
        project.ActivateConfiguration(second);
        project.AddPretask("Finish");
        project.AddPretask("Prepare");
        project.SetInputValue("LaunchInput", "caption", "second");
        project.SetInputValue(first, "LaunchInput", "caption", "late-first");
        project = ProjectPlanModule.Open(directory, path);
        Require(project.ActiveConfigurationId == second
            && project.CreateRunStartAttempt().Plan.Items.Select(item => item.TaskName)
                .SequenceEqual(["Prepare", "Finish"])
            && project.CreateRunStartAttempt().Plan.Items[0].ResolvedOptions.GetProperty("LaunchInput")
                .GetProperty("caption").GetString() == "second",
            "前置任务选择和参数必须独立持久化；来源配置的迟到编辑不能修改当前配置。");
        project.ActivateConfiguration(first);
        Require(project.SelectedPretaskNames.SequenceEqual(["Prepare"])
            && project.CreateRunStartAttempt().Plan.Items[0].ResolvedOptions.GetProperty("LaunchInput")
                .GetProperty("caption").GetString() == "late-first",
            "重新激活配置必须恢复该配置的前置任务与参数。");
        project.RemovePretask("Prepare");
        project = ProjectPlanModule.Open(directory, path);
        Require(project.SelectedPretaskNames.Count == 0
            && project.Configurations.Single(config => config.Id == first).ExplicitOptions.ContainsKey("LaunchInput"),
            "移除前置任务不能清掉已保存的选项意图。");
        project.AddPretask("Prepare");
        Require(project.GetConfiguration("Prepare", PlanItemKind.Pretask).TaskOptions.Single()
                .ActiveChildren.Single().Inputs.Single(input => input.Name == "caption").Value == "late-first",
            "移除并重新添加前置任务后必须恢复显式参数。");
    }

    private static void VerifyInvalidPretaskDefinitions(string testDirectory)
    {
        var directory = CreatePretaskFixture(Path.Combine(testDirectory, "invalid-source"), "[]");
        var source = File.ReadAllText(Path.Combine(directory, "interface.json"));
        var invalidDeclarations = new[] {
            "null", "false", "\"program\"", "[null]", "[1]", "{}",
            """{"exec":""}""",
            """{"exec":"fixture","args":"invalid"}""",
            """{"exec":"fixture","args":[1]}""",
            """{"exec":"fixture","args":["\u0000"]}""",
            """{"exec":"fixture\u0000"}""",
            """{"exec":"fixture","option":["MissingOption"]}""",
            """{"exec":"fixture","controller":"Win32"}""",
            """{"exec":"fixture","resource":[false]}""",
            """{"exec":"fixture","unknown":true}""",
            """[{"exec":"duplicate"},{"name":"duplicate","exec":"other"}]"""
        };
        for (var index = 0; index < invalidDeclarations.Length; index++) {
            var declaration = invalidDeclarations[index];
            var expectedError = declaration == """{"exec":""}""" ? "exec" : "pretask";
            VerifyRejectedProjectInterface(testDirectory, source, $"pretask-invalid-{index}", expectedError,
                root => root["pretask"] = JsonNode.Parse(declaration));
        }
    }

    // Drive- and root-relative exec paths fail when the PI loads, not at Start; full paths, UNC included, are kept.
    private static void VerifyPretaskExecutablePaths(string testDirectory)
    {
        var source = File.ReadAllText(Path.Combine(
            CreatePretaskFixture(Path.Combine(testDirectory, "exec-source"), "[]"), "interface.json"));
        foreach (var (name, executable) in new[] {
            ("drive-relative", "C:fixture.exe"), ("root-relative", "\\tools\\fixture.exe")
        }) {
            VerifyRejectedProjectInterface(testDirectory, source, $"exec-{name}", "$.pretask[0].exec 必须是完整路径",
                root => root["pretask"] = new JsonObject { ["exec"] = executable });
        }
        foreach (var (name, executable) in new[] {
            ("drive", "C:\\fixture\\tool.exe"), ("unc", "\\\\server\\share\\tool.exe")
        }) {
            var directory = CreatePretaskFixture(Path.Combine(testDirectory, $"exec-{name}"),
                new JsonObject { ["name"] = "Full", ["exec"] = executable }.ToJsonString());
            var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
            project.AddPretask("Full");
            Require(project.CreateRunStartAttempt().Plan.Items[0].Program!.Executable == executable,
                "完整路径与 UNC 路径的前置程序应加载成功并原样进入计划。");
        }
    }

    private static string CreatePretaskFixture(string testDirectory, string declaration)
    {
        var directory = CreateProjectFixture(testDirectory);
        var path = Path.Combine(directory, "interface.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["pretask"] = JsonNode.Parse(declaration);
        var options = root["option"]!.AsObject();
        options["StartupMode"] = JsonNode.Parse("""
            {"type":"select","default_case":"Auto","cases":[
              {"name":"Auto","option":["LaunchInput"],"pipeline_override":{"PretaskOnly":{"mode":"auto"}}},
              {"name":"Manual"}
            ]}
            """);
        options["StartupGate"] = JsonNode.Parse("""
            {"type":"switch","default_case":"On","cases":[{"name":"On"},{"name":"Off"}]}
            """);
        options["LaunchInput"] = JsonNode.Parse("""
            {"type":"input","inputs":[
              {"name":"caption","default":"","pipeline_type":"string"},
              {"name":"attempts","default":"3","pipeline_type":"int"},
              {"name":"enabled","default":"true","pipeline_type":"bool"}
            ],"pipeline_override":{"PretaskOnly":{"caption":"{caption}","attempts":"{attempts}","enabled":"{enabled}"}}}
            """);
        File.WriteAllText(path, root.ToJsonString());
        return directory;
    }
}
