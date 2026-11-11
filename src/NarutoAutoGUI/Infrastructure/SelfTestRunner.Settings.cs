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
    // Settings whose actions do nothing, for tests that only check toggles and persisted values.
    private static ApplicationSettings CreateIdleSettings(
        string directory, AppLogger logger, AutostartTask? autostart = null) =>
        new(directory, logger, () => Task.CompletedTask, () => Task.CompletedTask, () => { },
            () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, () => { },
            _ => { }, _ => { }, autostart ?? InertAutostart());

    private static void VerifyClosePreference(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "close-preference");
        var path = Path.Combine(directory, "config", "close-to-tray.txt");
        ApplicationSettings Create() => CreateIdleSettings(directory, logger);
        var settings = Create();
        if (!settings.CloseToTray.Value || File.Exists(path)) {
            throw new InvalidOperationException("关闭偏好默认隐藏到托盘，读取不得创建文件。");
        }
        settings.CloseToTray.Value = false;
        if (Create().CloseToTray.Value || File.ReadAllText(path) != "false") {
            throw new InvalidOperationException("重启必须恢复直接退出偏好。");
        }
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            settings.CloseToTray.Value = true;
            if (settings.CloseToTray.Value || string.IsNullOrEmpty(settings.CloseToTray.Status)) {
                throw new InvalidOperationException("关闭偏好保存失败必须恢复原值并提示。");
            }
        }
        settings.CloseToTray.Value = true;
        if (!Create().CloseToTray.Value || settings.CloseToTray.Status != "") {
            throw new InvalidOperationException("隐藏到托盘偏好必须持久化，重试成功清除错误。");
        }
        var exits = 0;
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "close-routing"), (window, _) => {
            var toggle = SettingsPageFor(window).Sections.SelectMany(section => section.Items)
                .Single(item => item.Definition.SettingKey == "application.closeToTray").Toggle!;
            window.Close();
            PumpOnboarding();
            if (window.IsVisible || exits != 0) {
                throw new InvalidOperationException("默认关闭只能隐藏到托盘。");
            }
            window.Show();
            toggle.Value = false;
            window.Close();
            PumpOnboarding();
            if (!window.IsVisible || exits != 1) {
                throw new InvalidOperationException("直接关闭必须请求安全退出，取消退出时保留窗口。");
            }
            window.SetExitInProgress(true);
            window.Close();
            PumpOnboarding();
            if (!window.IsVisible || exits != 1) {
                throw new InvalidOperationException("退出期间不能重复请求退出或隐藏窗口。");
            }
            window.SetExitInProgress(false);
        }, requestExit: () => { exits++; return Task.CompletedTask; });
    }

    private static void VerifyDeclarativeSettings(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "settings-bindings");
        var calls = new List<string>();
        var settings = new ApplicationSettings(directory, logger,
            () => Called("update.check"), () => Called("diagnostics.export"), () => calls.Add("onboarding.replay"),
            () => Called("support.openAfdian"), () => Called("childSession.credentials"),
            () => Called("diagnostics.clear"), () => calls.Add("welcome.view"), _ => { }, _ => { }, InertAutostart());
        var page = settings.Page;
        var items = page.Sections.SelectMany(section => section.Items).ToArray();
        if (items.Single(item => item.Definition.SettingKey == "update.checkOnStartup").Toggle
                != settings.CheckOnStartup
            || items.Single(item => item.Definition.ValueKey == "update.currentVersion").Value
                != settings.CurrentVersion) {
            throw new InvalidOperationException("内置 Settings Definition 的 section/item/key 映射不完整。");
        }
        foreach (var item in items.Where(item => item.Action is not null)) {
            item.Action!.ExecuteAsync().GetAwaiter().GetResult();
        }
        if (!calls.Order().SequenceEqual(["childSession.credentials", "diagnostics.clear", "diagnostics.export",
                "onboarding.replay", "support.openAfdian", "update.check", "welcome.view"])) {
            throw new InvalidOperationException("Settings action 必须只调用对应的已注册业务入口。");
        }
        settings.CheckUpdate.IsEnabled = false;
        settings.CheckUpdate.ExecuteAsync().GetAwaiter().GetResult();
        if (calls.Count != 7 || settings.CheckUpdate.CanExecute(null)) {
            throw new InvalidOperationException("禁用的 Settings action 不能执行。");
        }

        var preference = Path.Combine(directory, "config", "update-check.txt");
        settings.LoadUpdatePreference();
        if (!settings.CheckOnStartup.Value || File.Exists(preference)) {
            throw new InvalidOperationException("缺少更新偏好时应默认开启，加载/绑定不得写文件。");
        }
        settings.CheckOnStartup.Value = false;
        var reopened = CreateIdleSettings(directory, logger);
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
            {"sections":[{"id":"s","title":"Section","icon":"Info20","description":"Description",
             "items":[
              {"id":"t","type":"toggle","icon":"Info20","title":"Toggle","settingKey":"update.checkOnStartup"},
              {"id":"a","type":"action","icon":"Key20","title":"Action","buttonText":"Go","actionId":"update.check"},
              {"id":"i","type":"info","icon":"Tag20","joinPrevious":true,"title":"Info","description":"Static"},
              {"id":"h","type":"about","title":"About"}
            ]}]}
            """;
        var sample = settings.Registry.Bind(SettingsDefinition.Parse(valid));
        if (sample.Sections[0].Definition.Description != "Description"
            || sample.Sections[0].Items[2].Definition.Description != "Static") {
            throw new InvalidOperationException("Section 和静态 info 的说明必须保留。");
        }
        if (!sample.Sections[0].Cards.Select(card => card.Count).SequenceEqual([1, 2, 1])) {
            throw new InvalidOperationException("joinPrevious 的设置项必须与上一项合并到同一张卡片。");
        }
        ExpectInvalid(valid.Replace("\"toggle\"", "\"expression\""));
        ExpectInvalid(valid.Replace("\"settingKey\"", "\"expression\""));
        ExpectInvalid(valid.Replace("update.checkOnStartup", "unknown.setting"));
        ExpectInvalid(valid.Replace("update.check\"", "unknown.action\""));
        ExpectInvalid(valid.Replace("\"title\":\"Toggle\",", ""));
        ExpectInvalid(valid.Replace("\"id\":\"i\"", "\"id\":\"t\""));
        ExpectInvalid(valid.Replace("\"description\":\"Static\"", "\"valueKey\":\"unknown.value\""));
        ExpectInvalid(valid.Replace("{\"sections\"", "{\"title\":\"Settings\",\"sections\""));
        ExpectInvalid(valid.Replace("\"Key20\"", "\"NoSuchIcon\""));
        ExpectInvalid(valid.Replace("\"icon\":\"Key20\",", ""));
        ExpectInvalid(valid.Replace("\"icon\":\"Info20\",\"description\"", "\"description\""));
        ExpectInvalid(valid.Replace("\"type\":\"toggle\",", "\"type\":\"toggle\",\"joinPrevious\":true,"));
        VerifySettingsUpdateIntegration(logger, testDirectory);
        VerifyClosePreference(logger, testDirectory);
        VerifyFrameworkPreferences(logger, testDirectory);

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
            var toggle = SettingsVisualDescendants(view).OfType<Wpf.Ui.Controls.ToggleSwitch>()
                .Single(control => System.Windows.Automation.AutomationProperties.GetAutomationId(control)
                    == "startup-update-check");
            toggle.IsChecked = true;
            if (File.ReadAllText(Path.Combine(directory, "config", "update-check.txt")) != "true") {
                throw new InvalidOperationException("Toggle renderer 必须将选择传给持久化 seam。");
            }
            toggle.IsChecked = false;
            foreach (var key in new[] { "saveOnError", "saveDraw", "debugMode" }) {
                var item = page.Sections.SelectMany(section => section.Items)
                    .Single(item => item.Definition.SettingKey == "framework." + key);
                var control = SettingsVisualDescendants(view).OfType<Wpf.Ui.Controls.ToggleSwitch>()
                    .Single(control => System.Windows.Automation.AutomationProperties.GetAutomationId(control)
                        == item.Definition.Id);
                var previous = item.Toggle!.Value;
                control.IsChecked = !previous;
                if (item.Toggle.Value == previous) {
                    throw new InvalidOperationException("框架开关必须经 TwoWay 绑定保存用户选择。");
                }
            }
            var action = SettingsActionFor(window, "update.check");
            var button = SettingsButtonFor(window, "update.check");
            if (version.Text == "—" || button.Command != action) {
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
            if (version.Text != "7.8.9" || action.Status != "发现新版本，可查看更新。"
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
                || CanonicalDigest.ComputePlanDigestV2(afterPlan) != CanonicalDigest.ComputePlanDigestV2(plan)) {
                throw new InvalidOperationException("Settings 操作不能影响 Home、MaaNOP 配置、PI 或 RunPlan。");
            }
        }, checkForUpdate: _ => { checks++; return check.Task; });
    }

    // The list must lead to every section, and scrolling must move the highlight back with the content.
    private static void VerifySettingsNavigation(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "settings-navigation"), (window, _) => {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            ClickOnboarding(window, "SettingsNavigationItem");
            var view = (SettingsView)window.FindName("SettingsView");
            var list = (System.Windows.Controls.ListBox)view.FindName("SectionNavigation");
            var scroller = (ScrollViewer)view.FindName("SectionScroller");
            var sections = SettingsPageFor(window).Sections;
            Require(list.SelectedIndex == 0, "进入设置时目录应选中第一个分类。");

            double HeadingTop(int index) => SettingsVisualDescendants(scroller).OfType<TextBlock>()
                .First(text => text.Text == sections[index].Definition.Title)
                .TranslatePoint(default, scroller).Y;
            foreach (var index in Enumerable.Range(0, sections.Count).Reverse()) {
                list.SelectedIndex = index;
                PumpUntil(() => HeadingTop(index) is >= 0 and <= 60,
                    $"点击目录“{sections[index].Definition.Title}”应滚动到该分类。");
                Require(list.SelectedIndex == index, "目录跳转后不能被滚动跟随改回其他分类。");
            }
            scroller.ScrollToEnd();
            PumpUntil(() => list.SelectedIndex == sections.Count - 1, "滚动到底部时目录应选中最后一个分类。");
            scroller.ScrollToTop();
            PumpUntil(() => list.SelectedIndex == 0, "滚动回顶部时目录应选中第一个分类。");
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            first.Focus();
            first.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(first), 0, System.Windows.Input.Key.Down) {
                RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent
            });
            PumpUntil(() => list.SelectedIndex == 1 && HeadingTop(1) is >= 0 and <= 60, "方向键应能切换目录并滚动到该分类。");
            if (Environment.GetEnvironmentVariable("NARUTO_SUPPORT_QA_DIR") is { Length: > 0 } output) {
                window.Width = 1180;
                window.Height = 760;
                PumpOnboarding();
                Directory.CreateDirectory(output);
                SaveScreenshot(window, Path.Combine(output, "settings-default.png"));
            }
        });
    }

    // Failure details reach the log but never the status the user reads.
    private static void VerifyUpdateLog(string testDirectory)
    {
        using var logger = new AppLogger(Path.Combine(testDirectory, "update-log"));
        var written = new List<string>();
        logger.EntryWritten += (_, entry) => written.Add(entry.Message);
        void Expect(string failure, params string[] lines)
        {
            Require(lines.All(line => written.Any(text => text.StartsWith(line, StringComparison.Ordinal))),
                $"{failure}：{string.Join(" | ", written)}");
            written.Clear();
        }
        static IOException EngineFailure()
        {
            var failure = new IOException("界面文字");
            failure.Data[UpdateEngineClient.DiagnosticsKey] = "exit=1 code=network_error";
            return failure;
        }

        var check = new TaskCompletionSource<EngineCheckResult>();
        var prepare = new TaskCompletionSource<string>();
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "update-log-window"), (window, _) => {
            var action = SettingsActionFor(window, "update.check");
            var finished = action.ExecuteAsync();
            check.SetException(EngineFailure());
            PumpOnboarding();
            finished.GetAwaiter().GetResult();
            Expect("检查失败应写出 Engine 诊断", "检查更新 | enter", "检查更新 | leave, ",
                "检查更新失败 [engine=exit=1 code=network_error]");
            Require(action.Status == "界面文字", $"诊断不应出现在界面：{action.Status}");

            check = new TaskCompletionSource<EngineCheckResult>();
            check.SetResult(new EngineCheckResult("7.8.9", new EngineUpdate("8.0.0", "notes", "opaque")));
            action.ExecuteAsync().GetAwaiter().GetResult();
            PumpOnboarding();
            written.Clear();

            ClickOnboarding(window, "DownloadUpdateButton");
            prepare.SetException(EngineFailure());
            PumpOnboarding();
            Expect("准备失败应写目标版本、下载量和诊断", "准备更新 [target=8.0.0] | enter",
                "准备更新失败 [target=8.0.0] [downloaded=10/20] [engine=exit=1 code=network_error]");
            Require(((TextBlock)window.FindName("UpdateDownloadStatus")).Text == "界面文字", "诊断不应出现在下载状态中。");

            // Progress<T> posts its callback, so a prepare that ends at once finishes before that callback has run.
            prepare = new TaskCompletionSource<string>();
            prepare.SetException(EngineFailure());
            ClickOnboarding(window, "DownloadUpdateButton");
            Expect("立即失败时也应写出已报告的下载量",
                "准备更新失败 [target=8.0.0] [downloaded=10/20] [engine=exit=1 code=network_error]");


        }, checkForUpdate: _ => check.Task, prepareUpdate: (descriptor, progress, cancellation) => {
            Require(descriptor == "opaque", "准备更新应使用检查结果的描述符。");
            progress.Report(new EngineProgress("download", 10, 20, 5));
            var current = prepare;
            cancellation.Register(() => current.TrySetCanceled(cancellation));
            return current.Task;
        });
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
