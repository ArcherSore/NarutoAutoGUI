using System.Text.Json.Nodes;
using System.Windows.Automation;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    // An input whose default fails its own verify is a field the user must fill in: it loads, other parameters still
    // save, and only Start is blocked until a valid value is saved.
    private static void VerifyRequiredInputDefault(AppLogger logger, string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "required-input"));
        AddRequiredInput(directory);
        var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "config.json"));
        ProjectOptionEditor Recipient() =>
            project.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == "Recipient");
        project.SetSelectedCase("Mode", "Minimal");
        Require(Recipient().Inputs.Single().Error == "Fill in the recipient"
            && Recipient().StartBlocker == "Fill in the recipient"
            && Throws<InvalidOperationException>(() => project.CreateRunStartAttempt())
            && project.CheckInputValue("Recipient", "recipient", " ") is not null,
            "默认值未通过 verify 时项目仍应加载、其他参数可保存，但不能开始运行，也不能保存空白值。");
        project.SetInputValue("Recipient", "recipient", "Kakashi");
        Require(Recipient().StartBlocker is null && project.CreateRunStartAttempt().Plan.Items.Single()
            .PipelineOverride.GetRawText().Contains("\"to\":\"Kakashi\"", StringComparison.Ordinal),
            "填写合法值后必须可以开始运行，并替换进 pipeline_override。");

        RunOnboardingScenario(logger, Path.Combine(testDirectory, "required-input-window"), (window, _) => {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            EndTour(window);
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            var editor = ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBox>()
                .Single(box => AutomationProperties.GetName(box) == "Recipient");
            ParameterRowPanel Row() => ConfigurationDescendants(items).OfType<ParameterRowPanel>()
                .Single(panel => ConfigurationDescendants(panel).Contains(editor));
            bool Note() => ConfigurationDescendants(Row()).OfType<System.Windows.Controls.TextBlock>()
                .Any(text => text.Text == "Fill in the recipient");
            Require(Row().IsInvalid && Note() && editor.BorderBrush == window.FindResource("Brush.Error")
                && (bool)typeof(MainWindow).GetMethod("RevealInvalidParameter", flags)!.Invoke(window, null)!,
                "未填写的必填输入必须标红、显示 pattern_msg，并阻止开始。");

            editor.Text = "Kakashi";
            typeof(MainWindow).GetMethod("CommitOptionInput", flags)!.Invoke(window, [editor]);
            Require(!Row().IsInvalid && !Note()
                && editor.ReadLocalValue(System.Windows.Controls.Control.BorderBrushProperty)
                    == System.Windows.DependencyProperty.UnsetValue,
                "保存合法值后红色标记、提示与边框必须消失。");
        }, AddRequiredInput);
    }

    private static void AddRequiredInput(string directory)
    {
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        json["task"]![0]!["option"] = new JsonArray("Mode", "Recipient");
        json["option"]!["Recipient"] = JsonNode.Parse("""
            {"type": "input", "label": "Recipient", "inputs": [
              {"name": "recipient", "default": "", "pipeline_type": "string", "verify": ".*\\S.*",
               "pattern_msg": "Fill in the recipient"}],
             "pipeline_override": {"Gift": {"to": "{recipient}"}}}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
    }
}
