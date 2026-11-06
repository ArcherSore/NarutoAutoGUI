using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyCheckboxOptions(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "checkbox"));
        AddCheckboxOptions(directory);
        var configPath = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, configPath);
        ProjectOptionEditor Option(ProjectPlanModule plan, string name) =>
            plan.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == name);
        string[] MergeOrder() => project.CreateRunStartAttempt().Plan.Items.Single().PipelineOverride.EnumerateArray()
            .Where(item => item.TryGetProperty("FeatureOrder", out _))
            .Select(item => item.GetProperty("FeatureOrder").GetProperty("last").GetString()!).ToArray();

        var features = Option(project, "Features");
        var resolved = project.CreateRunStartAttempt().Plan.Items.Single().ResolvedOptions;
        Require(features is { Kind: ProjectOptionKind.Checkbox, MinCount: 1, MaxCount: 2, IsExplicit: false }
            && features.SelectedCases.SequenceEqual(["Normal", "Charged"])
            && features.ActiveChildren.Single().Name == "ChargeTime"
            && resolved.GetProperty("Features").EnumerateArray().Select(item => item.GetString())
                .SequenceEqual(["Normal", "Charged"])
            && resolved.GetProperty("Single").GetArrayLength() == 0,
            "checkbox 默认值必须按 case 声明顺序生效，已勾选 case 的子选项随之展开。");
        Require(MergeOrder().SequenceEqual(["normal", "charged", "short"]),
            "已勾选 case 的 override 必须按声明顺序合并，子选项紧随其 case。");

        project.SetSelectedCases("Features", ["Combo", "Normal"]);
        var saved = project.Configurations[0].ExplicitOptions["Features"].GetProperty("SelectedCases");
        Require(saved.EnumerateArray().Select(item => item.GetString()).SequenceEqual(["Normal", "Combo"])
            && MergeOrder().SequenceEqual(["normal", "combo"]) && Option(project, "Features").ActiveChildren.Count == 0,
            "保存的勾选与合并顺序必须与勾选先后无关。");
        Require(Throws<ArgumentException>(() => project.SetSelectedCases("Features", ["Normal", "Charged", "Combo"]))
            && Throws<ArgumentException>(() => project.SetSelectedCases("Features", ["Missing"]))
            && Throws<ArgumentException>(() => project.SetSelectedCase("Features", "Normal")),
            "checkbox 不得保存超过 max_count、不存在的 case 或 select 形式的值。");

        project.SetSelectedCases("Features", []);
        Require(Option(project, "Features").IsBelowMinimum
            && Option(ProjectPlanModule.Open(directory, configPath), "Features").SelectedCases.Count == 0
            && Throws<InvalidOperationException>(() => project.CreateRunStartAttempt()),
            "少于 min_count 的勾选可以保存，但不能开始运行。");

        var source = File.ReadAllText(Path.Combine(directory, "interface.json"));
        VerifyRejectedProjectInterface(testDirectory, source, "checkbox-limits", "需满足 min_count ≤ max_count",
            root => root["option"]!["Features"]!["min_count"] = 3);
        VerifyRejectedProjectInterface(testDirectory, source, "checkbox-default",
            "default_case 引用了不存在的 case：Missing",
            root => root["option"]!["Features"]!["default_case"] = new JsonArray("Missing"));
        VerifyRejectedProjectInterface(testDirectory, source, "checkbox-default-max", "default_case 超过 max_count 2",
            root => root["option"]!["Features"]!["default_case"] = new JsonArray("Normal", "Charged", "Combo"));
    }

    private static void VerifyCheckboxEditor(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "checkbox-editor"), (window, _) => {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var project = ProjectOf(window);
            EndTour(window);
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            PumpOnboarding();
            SaveWindowScreenshot(window, "checkbox.png");
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            // Saving may rebuild the card, so each step looks the editor up again.
            ListBox Segments(string name) => ConfigurationDescendants(items).OfType<ListBox>()
                .Single(list => AutomationProperties.GetName(list) == name);
            ProjectCaseEditor Case(ListBox list, string name) =>
                list.Items.Cast<ProjectCaseEditor>().Single(item => item.Name == name);
            ProjectOptionEditor Option(string name) =>
                project.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == name);

            var features = Segments("Features");
            features.UpdateLayout();
            var normal = (ListBoxItem)features.ItemContainerGenerator.ContainerFromItem(Case(features, "Normal"));
            var combo = (ListBoxItem)features.ItemContainerGenerator.ContainerFromItem(Case(features, "Combo"));
            Require(features.SelectedItems.Count == 2 && Equals(normal.ToolTip, "Enable normal strike")
                && combo.ToolTip is null && AutomationProperties.GetName(normal) == "Normal strike",
                "多 case checkbox 必须显示为分段，有说明的 case 以 tooltip 显示说明。");

            features.SelectedItems.Add(Case(features, "Combo"));
            Require(Option("Features").SelectedCases.SequenceEqual(["Normal", "Charged"])
                && Segments("Features").SelectedItems.Count == 2, "达到 max_count 后多出的勾选必须被撤回。");
            Segments("Features").SelectedItems.Remove(Case(Segments("Features"), "Charged"));
            Segments("Features").SelectedItems.Remove(Case(Segments("Features"), "Normal"));
            var row = ConfigurationDescendants(items).OfType<ParameterRowPanel>()
                .Single(panel => ConfigurationDescendants(panel).Contains(Segments("Features")));
            Require(Option("Features").SelectedCases.Count == 0 && row.IsInvalid
                && ConfigurationDescendants(row).OfType<TextBlock>()
                    .Any(text => text.Text == "至少选择 1 项，否则不能开始任务")
                && ParameterInspector.ShowCases(Option("Features")) == "无"
                && (bool)typeof(MainWindow).GetMethod("RevealInvalidParameter", flags)!.Invoke(window, null)!,
                "少于 min_count 时必须保存、标红并阻止开始。");

            Segments("Tier").SelectedItems.Add(Case(Segments("Tier"), "Low"));
            Segments("Tier").SelectedItems.Add(Case(Segments("Tier"), "High"));
            Require(Option("Tier").SelectedCases.SequenceEqual(["High"]) && Segments("Tier").SelectedItems.Count == 1,
                "max_count 为 1 时，新勾选必须替换旧勾选。");

            Segments("Finish").SelectedItems.Add(Case(Segments("Finish"), "Enabled"));
            Require(Option("Single").SelectedCases.SequenceEqual(["Enabled"])
                && !ConfigurationDescendants(items).OfType<CheckBox>().Any(box => AutomationProperties.GetName(box)
                    == "Finish") && ParameterInspector.ShowCases(Option("Single")) == "Finish directly",
                "单 case checkbox 也必须显示为分段，摘要显示 case 标签。");
        }, AddCheckboxOptions);
    }

    // Adds checkbox options to RealTask: limits with a sub-option, max_count 1, and a single case.
    private static void AddCheckboxOptions(string directory)
    {
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        json["task"]![0]!["option"] = new JsonArray("Mode", "Features", "Tier", "Single");
        var definitions = json["option"]!.AsObject();
        definitions["Features"] = JsonNode.Parse("""
            {"type": "checkbox", "label": "Features", "min_count": 1, "max_count": 2,
             "default_case": ["Charged", "Normal"], "cases": [
              {"name": "Normal", "label": "Normal strike", "description": "Enable normal strike",
               "pipeline_override": {"FeatureOrder": {"last": "normal"}}},
              {"name": "Charged", "label": "Charged strike", "option": ["ChargeTime"],
               "pipeline_override": {"FeatureOrder": {"last": "charged"}}},
              {"name": "Combo", "label": "Combo strike", "pipeline_override": {"FeatureOrder": {"last": "combo"}}}]}
            """);
        definitions["ChargeTime"] = JsonNode.Parse("""
            {"type": "select", "label": "Charge time", "default_case": "Short", "cases": [
              {"name": "Short", "pipeline_override": {"FeatureOrder": {"last": "short"}}}, {"name": "Long"}]}
            """);
        definitions["Tier"] = JsonNode.Parse("""
            {"type": "checkbox", "label": "Tier", "max_count": 1, "cases": [
              {"name": "Low", "label": "≤1次"}, {"name": "High", "label": "≤3次"}]}
            """);
        definitions["Single"] = JsonNode.Parse("""
            {"type": "checkbox", "label": "Finish", "cases": [
              {"name": "Enabled", "label": "Finish directly", "description": "Skip the progress check"}]}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
    }
}
