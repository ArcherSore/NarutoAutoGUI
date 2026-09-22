using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Settings;
using NarutoAutoGUI.Updates;
using NarutoAutoGUI.Views;
using WpfButton = System.Windows.Controls.Button;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyDeclarativeSettings(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "settings-bindings");
        var calls = new List<string>();
        var settings = new ApplicationSettings(directory, logger,
            () => Called("update.check"), () => Called("diagnostics.export"), () => calls.Add("onboarding.replay"));
        var page = settings.Page;
        var items = page.Sections.SelectMany(section => section.Items).ToArray();
        if (!page.Sections.Select(section => section.Definition.Id)
                .SequenceEqual(["updates", "application", "diagnostics", "help"])
            || items.Count(item => item.Definition.Type == SettingsItemKind.Toggle) != 1
            || items.Count(item => item.Definition.Type == SettingsItemKind.Action) != 3
            || items.Count(item => item.Definition.Type == SettingsItemKind.Info) != 2
            || items.Single(item => item.Definition.SettingKey == "update.checkOnStartup").Toggle
                != settings.CheckOnStartup
            || items.Single(item => item.Definition.ValueKey == "update.currentVersion").Value
                != settings.CurrentVersion) {
            throw new InvalidOperationException("内置 Settings Definition 的 section/item/key 映射不完整。");
        }
        foreach (var item in items.Where(item => item.Action is not null)) {
            item.Action!.ExecuteAsync().GetAwaiter().GetResult();
        }
        if (!calls.SequenceEqual(["update.check", "diagnostics.export", "onboarding.replay"])) {
            throw new InvalidOperationException("Settings action 必须只调用对应的已注册业务入口。");
        }
        settings.CheckUpdate.IsEnabled = false;
        settings.CheckUpdate.ExecuteAsync().GetAwaiter().GetResult();
        if (calls.Count != 3 || settings.CheckUpdate.CanExecute(null)) {
            throw new InvalidOperationException("禁用的 Settings action 不能执行。");
        }

        var preference = Path.Combine(directory, "config", "update-check.txt");
        settings.LoadUpdatePreference();
        if (!settings.CheckOnStartup.Value || File.Exists(preference)) {
            throw new InvalidOperationException("缺少更新偏好时应默认开启，加载/绑定不得写文件。");
        }
        settings.CheckOnStartup.Value = false;
        var reopened = new ApplicationSettings(directory, logger, () => Task.CompletedTask,
            () => Task.CompletedTask, () => { });
        reopened.LoadUpdatePreference();
        if (File.ReadAllText(preference) != "false" || reopened.CheckOnStartup.Value) {
            throw new InvalidOperationException("Toggle 必须以原有格式持久化，并在重新加载后保留选择。");
        }
        reopened.CheckOnStartup.Value = true;
        if (File.ReadAllText(preference) != "true") {
            throw new InvalidOperationException("重新开启更新检查必须持久化 true。");
        }
        using (var locked = new FileStream(preference, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            reopened.CheckOnStartup.Value = false;
            if (reopened.CheckUpdate.Status != "无法保存更新设置，请重试。") {
                throw new InvalidOperationException("更新偏好写入失败必须显示现有错误提示。");
            }
        }
        if (File.ReadAllText(preference) != "true") {
            throw new InvalidOperationException("写入失败不能损坏已有更新偏好。");
        }

        const string valid = """
            {"title":"Settings","sections":[{"id":"s","title":"Section","description":"Description","items":[
              {"id":"t","type":"toggle","title":"Toggle","settingKey":"update.checkOnStartup"},
              {"id":"a","type":"action","title":"Action","buttonText":"Go","actionId":"update.check"},
              {"id":"i","type":"info","title":"Info","description":"Static"}
            ]}]}
            """;
        var sample = settings.Registry.Bind(SettingsDefinition.Parse(valid));
        if (sample.Sections[0].Definition.Description != "Description"
            || sample.Sections[0].Items[2].Definition.Description != "Static") {
            throw new InvalidOperationException("Section 和静态 info 的说明必须保留。");
        }
        ExpectInvalid(valid.Replace("\"toggle\"", "\"expression\""));
        ExpectInvalid(valid.Replace("\"settingKey\"", "\"expression\""));
        ExpectInvalid(valid.Replace("update.checkOnStartup", "unknown.setting"));
        ExpectInvalid(valid.Replace("update.check\"", "unknown.action\""));
        ExpectInvalid(valid.Replace("\"title\":\"Toggle\",", ""));
        ExpectInvalid(valid.Replace("\"id\":\"i\"", "\"id\":\"t\""));
        ExpectInvalid(valid.Replace("\"description\":\"Static\"", "\"valueKey\":\"unknown.value\""));
        VerifySettingsUpdateIntegration(logger, testDirectory);
        Console.WriteLine("SETTINGS SELF-TEST PASS: definition, registry, preference, actions, "
            + "dynamic state and isolation.");

        Task Called(string id)
        {
            calls.Add(id);
            return Task.CompletedTask;
        }

        void ExpectInvalid(string json)
        {
            try {
                settings.Registry.Bind(SettingsDefinition.Parse(json));
            } catch (Exception exception) when (exception is JsonException or InvalidDataException) {
                return;
            }
            throw new InvalidOperationException("无效 Settings Definition 或未注册 key 不得静默接受。");
        }
    }

    private static void VerifySettingsUpdateIntegration(AppLogger logger, string testDirectory)
    {
        var check = new TaskCompletionSource<EngineCheckResult>();
        var checks = 0;
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "settings-update"), (window, directory) => {
            var configPath = Path.Combine(directory, "config", "maanop-config.json");
            var config = File.ReadAllBytes(configPath);
            var interfacePath = Path.Combine(directory, "interface.json");
            var projectInterface = File.ReadAllBytes(interfacePath);
            var plan = ProjectPlanModule.Open(directory, configPath).CreateRunStartAttempt().Plan;
            var homeContext = ((FrameworkElement)window.FindName("HomeView")).DataContext;
            ClickOnboarding(window, "SettingsNavigationItem");
            var view = (SettingsView)window.FindName("SettingsView");
            var page = SettingsPageFor(window);
            var version = page.Sections.SelectMany(section => section.Items)
                .Single(item => item.Definition.ValueKey == "update.currentVersion").Value!;
            var toggle = SettingsVisualDescendants(view).OfType<System.Windows.Controls.CheckBox>().Single();
            toggle.IsChecked = true;
            if (File.ReadAllText(Path.Combine(directory, "config", "update-check.txt")) != "true") {
                throw new InvalidOperationException("Toggle renderer 必须将选择传给持久化 seam。");
            }
            toggle.IsChecked = false;
            var action = SettingsActionFor(window, "update.check");
            var button = SettingsButtonFor(window, "update.check");
            if (version.Text == "当前版本：—" || button.Command != action) {
                throw new InvalidOperationException("初始项目版本与检查更新 command 绑定不能丢失。");
            }
            button.Command.Execute(null);
            PumpOnboarding();
            if (checks != 1 || button.IsEnabled || action.Status != "正在检查更新…"
                || ((UIElement)window.FindName("UpdateOverlay")).Visibility != Visibility.Visible) {
                throw new InvalidOperationException("Settings 检查动作必须打开原弹窗、调用检查并同步忙状态。");
            }
            action.ExecuteAsync().GetAwaiter().GetResult();
            if (checks != 1) {
                throw new InvalidOperationException("检查更新期间不能重复调用 Engine。");
            }
            check.SetResult(new EngineCheckResult("7.8.9", new EngineUpdate("8.0.0", "notes", "opaque")));
            PumpOnboarding();
            if (version.Text != "当前版本：7.8.9" || action.Status != "发现新版本，可查看更新。"
                || !action.IsEnabled || !SettingsVisualDescendants(view).OfType<TextBlock>()
                    .Any(text => text.Text == version.Text)
                || !SettingsVisualDescendants(view).OfType<TextBlock>().Any(text => text.Text == action.Status)) {
                throw new InvalidOperationException("检查结果必须更新 Settings 的绑定文本并恢复按钮。");
            }
            check = new TaskCompletionSource<EngineCheckResult>();
            var failed = action.ExecuteAsync();
            check.SetException(new IOException("测试检查失败"));
            PumpOnboarding();
            failed.GetAwaiter().GetResult();
            if (action.Status != "测试检查失败"
                || ((TextBlock)window.FindName("UpdateResultMessage")).Text != action.Status) {
                throw new InvalidOperationException("Settings 与原更新弹窗必须显示同一检查错误。");
            }
            ClickOnboarding(window, "CloseUpdateButton");
            window.SetExitInProgress(true);
            if (action.CanExecute(null) || SettingsActionFor(window, "onboarding.replay").CanExecute(null)) {
                throw new InvalidOperationException("退出时更新与指引必须保持禁用。");
            }
            window.SetExitInProgress(false);
            ClickOnboarding(window, "HomeNavigationItem");
            var afterPlan = ProjectPlanModule.Open(directory, configPath).CreateRunStartAttempt().Plan;
            afterPlan = afterPlan with {
                CreatedAtUtc = plan.CreatedAtUtc,
                Items = afterPlan.Items.Select((item, index) => item with {
                    PlanItemId = plan.Items[index].PlanItemId
                }).ToArray()
            };
            if (((UIElement)window.FindName("HomeView")).Visibility != Visibility.Visible
                || ((FrameworkElement)window.FindName("HomeView")).DataContext != homeContext
                || !config.SequenceEqual(File.ReadAllBytes(configPath))
                || !projectInterface.SequenceEqual(File.ReadAllBytes(interfacePath))
                || CanonicalDigest.ComputePlanDigestV1(afterPlan) != CanonicalDigest.ComputePlanDigestV1(plan)) {
                throw new InvalidOperationException("Settings 操作不能影响 Home、MaaNOP 配置、PI 或 RunPlan。");
            }
        }, checkForUpdate: _ => { checks++; return check.Task; });
    }

    private static IEnumerable<DependencyObject> SettingsVisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in SettingsVisualDescendants(child)) {
                yield return descendant;
            }
        }
    }

    private static SettingsPageModel SettingsPageFor(MainWindow window)
        => (SettingsPageModel)((SettingsView)window.FindName("SettingsView")).DataContext;

    private static SettingsAction SettingsActionFor(MainWindow window, string actionId)
        => SettingsPageFor(window).Sections.SelectMany(section => section.Items)
            .Single(item => item.Definition.ActionId == actionId).Action!;

    private static WpfButton SettingsButtonFor(MainWindow window, string actionId)
        => SettingsVisualDescendants((SettingsView)window.FindName("SettingsView")).OfType<WpfButton>()
            .Single(button => button.Command == SettingsActionFor(window, actionId));
}
