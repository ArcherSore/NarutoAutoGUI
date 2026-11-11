using System.Text.Json.Nodes;
using System.Windows.Automation;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private const string SecretInputValue = "Hokage-pw-7";

    // A password reaches the pipeline and the pretask program as entered, but never the configuration file or the log.
    private static void VerifyPasswordInput(AppLogger logger, string testDirectory)
    {
        var directory = CreatePretaskFixture(Path.Combine(testDirectory, "password"), """
            {"name":"Prepare","exec":"prepare-fixture","option":["Login"]}
            """);
        AddSecretAndPathInputs(directory);
        var configPath = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, configPath);
        project.AddPretask("Prepare");
        var emptyAttempt = project.CreateRunStartAttempt();
        Require(emptyAttempt.Plan.Items.Count == 2 && emptyAttempt.RedactionValues.Count == 0,
            "没有 verify 的密码字段应允许为空并可以开始运行；空密码不需要脱敏。");
        project.SetInputValue("Login", "password", SecretInputValue);
        Require(!File.ReadAllText(configPath).Contains(SecretInputValue, StringComparison.Ordinal),
            "密码不得以明文写入配置文件。");

        var reopened = ProjectPlanModule.Open(directory, configPath);
        var input = reopened.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == "Login")
            .Inputs.Single();
        Require(input.IsPassword && input.Value == SecretInputValue, "重新打开配置后应读回原密码。");
        var attempt = reopened.CreateRunStartAttempt();
        var items = attempt.Plan.Items;
        Require(items.Single(item => item.Kind == PlanItemKind.MaaTask).PipelineOverride.GetRawText()
                .Contains(SecretInputValue, StringComparison.Ordinal)
            && items.Single(item => item.Kind == PlanItemKind.Pretask).Program!.Arguments[^1]
                .Contains(SecretInputValue, StringComparison.Ordinal),
            "pipeline_override 与前置程序参数必须拿到密码原文。");
        // Telemetry redacts exactly the passwords this Run uses, found through PI's inputs[].password.
        Require(attempt.RedactionValues.SequenceEqual([SecretInputValue])
            && reopened.CreateRunStartAttempt(startupOnly: true).RedactionValues.SequenceEqual([SecretInputValue]),
            "Run 用到的每个非空密码（含前置任务选项）都必须列入 RedactionValues，且不重复。");
        var written = new List<LogEntry>();
        void Capture(object? sender, LogEntry entry) => written.Add(entry);
        logger.EntryWritten += Capture;
        try {
            RunResultLog.WritePlan(logger, attempt, "配置 1", startupOnly: false);
        } finally {
            logger.EntryWritten -= Capture;
        }
        Require(written.Count != 0 && written.All(entry => !entry.Message.Contains(SecretInputValue,
                StringComparison.Ordinal)),
            "Run 计划日志不得包含密码原文。");

        VerifyUndecryptablePasswords(testDirectory);
        var source = File.ReadAllText(Path.Combine(directory, "interface.json"));
        VerifyRejectedProjectInterface(testDirectory, source, "password-default", "不能声明 default",
            root => root["option"]!["Login"]!["inputs"]![0]!["default"] = "leaked");
        VerifyRejectedProjectInterface(testDirectory, source, "password-preset", "不能填写密码字段",
            root => root["preset"] = JsonNode.Parse("""
                [{"name":"Daily","task":[{"name":"RealTask","option":{"Login":{"password":"leaked"}}}]}]
                """));
        VerifyRejectedProjectInterface(testDirectory, source, "unknown-input-type", "不支持 input_type",
            root => root["option"]!["Paths"]!["inputs"]![0]!["input_type"] = "image");
    }

    // A configuration copied from another Windows user cannot be decrypted. Its required passwords read as unfilled:
    // Start is blocked, yet each can be entered again while the others are still missing.
    private static void VerifyUndecryptablePasswords(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "password-recovery"));
        AddSecretAndPathInputs(directory);
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        json["option"]!["Login"] = JsonNode.Parse("""
            {"type": "input", "label": "Login", "inputs": [
              {"name": "first", "password": true, "verify": ".+", "pattern_msg": "Required"},
              {"name": "second", "password": true, "verify": ".+", "pattern_msg": "Required"}],
             "pipeline_override": {"Login": {"first": "{first}", "second": "{second}"}}}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
        var configPath = Path.Combine(directory, "config.json");
        var project = ProjectPlanModule.Open(directory, configPath);
        project.SetInputValue("Login", "first", SecretInputValue);
        project.SetInputValue("Login", "second", SecretInputValue);

        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        var inputs = config["Configurations"]![0]!["ExplicitOptions"]!["Login"]!["Inputs"]!;
        inputs["first"] = "bm90LWRwYXBp";
        inputs["second"] = "bm90LWRwYXBp";
        File.WriteAllText(configPath, config.ToJsonString());
        var reopened = ProjectPlanModule.Open(directory, configPath);
        var login = reopened.GetConfiguration("RealTask").TaskOptions.Single(option => option.Name == "Login");
        Require(login.Inputs.All(input => input.Value.Length == 0 && input.Error is not null)
            && Throws<InvalidOperationException>(() => reopened.CreateRunStartAttempt()),
            "无法解密的必填密码应显示为未填写，并阻止开始运行。");
        reopened.SetInputValue("Login", "first", "renewed");
        reopened.SetInputValue("Login", "second", "renewed");
        Require(reopened.CreateRunStartAttempt().Plan.Items.Single().PipelineOverride.GetRawText()
                .Contains("renewed", StringComparison.Ordinal),
            "无法解密的密码必须能逐个重新填写，填完后可以开始运行。");
    }

    // A password is masked in its editor and in the folded summary; a path input can be typed or picked, except a
    // password path, which stays a plain password box.
    private static void VerifyPasswordAndPathEditors(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "secret-input-window"), (window, _) => {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            EndTour(window);
            SetOnboardingField(window, "_expandedTaskName", "RealTask");
            InvokeOnboarding(window, "RenderTaskPlan");
            var items = (System.Windows.DependencyObject)window.FindName("PlanItemsPanel");
            var boxes = ConfigurationDescendants(items).OfType<System.Windows.Controls.TextBox>().ToArray();
            System.Windows.Controls.TextBox Editor(string name) =>
                boxes.Single(box => AutomationProperties.GetName(box) == name);
            bool HasBrowse(System.Windows.Controls.TextBox editor) =>
                ((System.Windows.Controls.Panel)editor.Parent).Children.OfType<System.Windows.Controls.Button>().Any();
            Require(HasBrowse(Editor("Paths · Launcher")) && HasBrowse(Editor("Paths · Folder")),
                "文件与文件夹输入必须提供选择按钮。");

            var password = Editor("Login") as Wpf.Ui.Controls.PasswordBox;
            Require(password is not null && !HasBrowse(password), "密码字段必须使用不带选择按钮的密码框。");
            password!.Password = SecretInputValue;
            typeof(MainWindow).GetMethod("CommitOptionInput", flags)!.Invoke(window, [password]);
            Require(!password.Text.Contains(SecretInputValue, StringComparison.Ordinal),
                "密码框不得显示密码原文。");
            SaveWindowScreenshot(window, "secret-and-path-inputs.png");

            SetOnboardingField(window, "_expandedTaskName", null!);
            InvokeOnboarding(window, "RenderTaskPlan");
            var texts = ConfigurationDescendants(items).OfType<System.Windows.Documents.Run>()
                .Select(run => run.Text).ToArray();
            Require(texts.Any(text => text.StartsWith("password：", StringComparison.Ordinal) && text.Length > 9)
                && texts.All(text => !text.Contains(SecretInputValue, StringComparison.Ordinal)),
                "折叠后的参数摘要应提示已填写，但不得显示密码原文。");
        }, AddSecretAndPathInputs);
    }

    private static void AddSecretAndPathInputs(string directory)
    {
        var interfacePath = Path.Combine(directory, "interface.json");
        var json = JsonNode.Parse(File.ReadAllText(interfacePath))!;
        json["task"]![0]!["option"] = new JsonArray("Mode", "Login", "Paths");
        json["option"]!["Login"] = JsonNode.Parse("""
            {"type": "input", "label": "Login", "inputs": [
              {"name": "password", "input_type": "file", "password": true, "pipeline_type": "string"}],
             "pipeline_override": {"Login": {"password": "{password}"}}}
            """);
        json["option"]!["Paths"] = JsonNode.Parse("""
            {"type": "input", "label": "Paths", "inputs": [
              {"name": "launcher", "label": "Launcher", "default": "", "input_type": "file"},
              {"name": "folder", "label": "Folder", "default": "", "input_type": "directory"}],
             "pipeline_override": {"Launch": {"path": "{launcher}", "folder": "{folder}"}}}
            """);
        File.WriteAllText(interfacePath, json.ToJsonString());
    }
}
