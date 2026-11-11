using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ProjectModel;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void EditInterface(string directory, Action<JsonObject> edit)
    {
        var path = Path.Combine(directory, "interface.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    private static void SetWelcome(string directory, JsonNode? welcome, string? label = null) =>
        EditInterface(directory, root => {
            root["welcome"] = welcome;
            if (label is not null) {
                root["label"] = label;
            }
        });

    private static ProjectWelcome? ResolveFixtureWelcome(string directory, List<string>? warnings = null) =>
        ProjectPlanModule.Open(directory, Path.Combine(directory, "welcome-config.json"))
            .ResolveWelcome(message => warnings?.Add(message));

    private static void VerifyWelcomeContent(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "welcome-content"));
        var source = File.ReadAllText(Path.Combine(directory, "interface.json"));
        var invalidValues = new JsonNode[] { 1, new JsonArray("正文", 1), new JsonObject() };
        for (var index = 0; index < invalidValues.Length; index++) {
            var invalid = invalidValues[index];
            VerifyRejectedProjectInterface(testDirectory, source, $"welcome-invalid-{index}",
                "$.welcome 必须是 string 或 string 数组", root => root["welcome"] = invalid.DeepClone());
        }

        Require(!ProjectPlanModule.Open(directory, Path.Combine(directory, "welcome-config.json")).HasWelcome
            && ResolveFixtureWelcome(directory) is null, "未声明 welcome 的项目不得有公告。");
        foreach (var empty in new JsonNode?[] { null, "", new JsonArray(), new JsonArray("") }) {
            SetWelcome(directory, empty);
            Require(ResolveFixtureWelcome(directory) is null, "没有实际内容的 welcome 不得弹出公告。");
        }

        Directory.CreateDirectory(Path.Combine(directory, "notes"));
        File.WriteAllText(Path.Combine(directory, "notes", "update.md"), "# 文件公告");
        File.WriteAllText(Path.Combine(directory, "NOTICE"), "无扩展名公告");
        SetWelcome(directory, new JsonArray("直接文本", "notes/update.md", "./NOTICE", "$notice"), "项目标题");
        var welcome = ResolveFixtureWelcome(directory)!;
        Require(welcome.Title == "项目标题", "公告标题应使用项目 label。");
        Require(welcome.Contents.SequenceEqual(["直接文本", "# 文件公告", "无扩展名公告", "notice"]),
            $"公告必须按声明顺序解析文本和文件：{string.Join(" | ", welcome.Contents)}");

        var outside = Path.Combine(Directory.GetParent(directory)!.FullName, "outside.md");
        File.WriteAllText(outside, "项目外内容");
        SetWelcome(directory, new JsonArray("../outside.md", "missing/file.md"));
        var warnings = new List<string>();
        welcome = ResolveFixtureWelcome(directory, warnings)!;
        Require(welcome.Contents.SequenceEqual(["../outside.md", "missing/file.md"]) && warnings.Count == 2,
            "项目外或不存在的文件不得读取，应按文本显示并记录日志。");

        string HashOf(JsonNode node)
        {
            SetWelcome(directory, node);
            return ResolveFixtureWelcome(directory)!.Hash;
        }
        File.WriteAllText(Path.Combine(directory, "first.md"), "第一条");
        File.WriteAllText(Path.Combine(directory, "second.md"), "第二条");
        var hash = HashOf(new JsonArray("first.md", "second.md"));
        Require(hash == HashOf(new JsonArray("first.md", "second.md")), "内容未变时已读记录必须保持有效。");
        Require(hash != HashOf(new JsonArray("second.md", "first.md")), "公告重排必须再次提示。");
        Require(hash != HashOf(new JsonArray("second.md")), "公告删减必须再次提示。");
        Require(hash != HashOf(new JsonArray("first.md", "second.md", "第三条")), "公告新增必须再次提示。");
        Require(HashOf(new JsonArray("ab", "c")) != HashOf(new JsonArray("a", "bc")), "公告边界必须参与已读判断。");
        SetWelcome(directory, new JsonArray("first.md", "second.md"));
        File.WriteAllText(Path.Combine(directory, "second.md"), "新版第二条");
        Require(hash != ResolveFixtureWelcome(directory)!.Hash, "路径不变但文件内容更新时必须再次提示。");
    }

    private static void VerifyWelcomeDialog(AppLogger logger, string testDirectory)
    {
        static Visibility OverlayOf(MainWindow window) => Named<UIElement>(window, "WelcomeOverlay").Visibility;
        static string DocumentText(MainWindow window)
        {
            var document = Named<FlowDocumentScrollViewer>(window, "WelcomeViewer").Document;
            return new TextRange(document.ContentStart, document.ContentEnd).Text;
        }
        var longNotice = string.Join("\n\n", Enumerable.Range(1, 40).Select(line => $"第 {line} 行公告内容"));
        string? acknowledged = null;
        Action<string> Acknowledged(string second) => directory => {
            SetWelcome(directory, new JsonArray("第一条公告", second), "项目标题");
            Directory.CreateDirectory(Path.Combine(directory, "config"));
            File.WriteAllText(Path.Combine(directory, "config", "welcome-shown.txt"), acknowledged);
        };

        RunOnboardingScenario(logger, Path.Combine(testDirectory, "welcome-first"), (window, directory) => {
            var hashPath = Path.Combine(directory, "config", "welcome-shown.txt");
            Require(OverlayOf(window) == Visibility.Visible, "内容未读时启动必须弹出公告。");
            Require(Named<TextBlock>(window, "WelcomeTitle").Text == "项目标题", "弹窗标题应显示项目 label。");
            var text = DocumentText(window);
            Require(text.IndexOf("第一条公告", StringComparison.Ordinal) >= 0
                && text.IndexOf("第一条公告", StringComparison.Ordinal) < text.IndexOf("第二条公告", StringComparison.Ordinal),
                "多条公告必须按声明顺序显示。");
            Require(!File.Exists(hashPath), "尚未关闭的公告不得记为已读。");
            var dismiss = Named<System.Windows.Controls.Button>(window, "WelcomeDismissButton");
            Require(SettingsVisualDescendants(dismiss).OfType<TextBlock>().Single().Foreground == dismiss.Foreground,
                "“我知道了”的文字必须使用按钮前景色，否则在主色背景上不可读。");

            CompleteOnboardingStartup(window);
            InvokeOnboarding(window, "ReevaluateOnboarding");
            PumpOnboarding(450);
            Require(Named<UIElement>(window, "OnboardingOverlay").Visibility == Visibility.Collapsed,
                "公告打开时不得出现新手指引。");

            ClickOnboarding(window, "WelcomeDismissButton");
            Require(OverlayOf(window) == Visibility.Collapsed, "点击“我知道了”必须关闭公告。");
            acknowledged = File.ReadAllText(hashPath);
            Require(acknowledged.Length > 0, "关闭公告必须记录已读。");
            WaitForOnboardingStep(window, 1, "配置自动化任务");
            EndTour(window);
            PumpOnboarding();

            var view = SettingsActionFor(window, "welcome.view");
            Require(view.IsVisible, "有公告时设置页必须提供查看入口。");
            File.WriteAllText(hashPath, "manual-view-must-not-change-this");
            view.Execute(null);
            PumpOnboarding();
            Require(OverlayOf(window) == Visibility.Visible, "设置页入口必须打开公告。");
            CaptureWelcome(window, "manual");
            ClickOnboarding(window, "WelcomeCloseButton");
            Require(OverlayOf(window) == Visibility.Collapsed
                && File.ReadAllText(hashPath) == "manual-view-must-not-change-this", "手动查看不得改变已读记录。");

            view.Execute(null);
            PumpOnboarding();
            ClickOnboarding(window, "WelcomeScrim");
            Require(OverlayOf(window) == Visibility.Collapsed, "点击遮罩必须关闭公告。");

            view.Execute(null);
            PumpOnboarding();
            var source = PresentationSource.FromVisual(window)!;
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            PumpOnboarding();
            Require(OverlayOf(window) == Visibility.Collapsed, "Esc 必须关闭公告。");
        }, beforeLoad: directory => SetWelcome(directory, new JsonArray("第一条公告", "第二条公告"), "项目标题"));

        RunOnboardingScenario(logger, Path.Combine(testDirectory, "welcome-read"), (window, _) => {
            Require(OverlayOf(window) == Visibility.Collapsed, "内容未变时启动不得再次弹出公告。");
            ClickOnboarding(window, "SettingsNavigationItem");
            PumpOnboarding();
            Require(SettingsButtonFor(window, "welcome.view").IsVisible, "已读后仍须能从设置页查看公告。");
            var scroller = SettingsVisualDescendants(Named<DependencyObject>(window, "SettingsView"))
                .OfType<ScrollViewer>().First();
            scroller.ScrollToEnd();
            CaptureWelcome(window, "settings");
        }, beforeLoad: Acknowledged("第二条公告"));

        RunOnboardingScenario(logger, Path.Combine(testDirectory, "welcome-updated"), (window, _) => {
            Require(OverlayOf(window) == Visibility.Visible, "公告内容更新后启动必须再次弹出。");
        }, beforeLoad: Acknowledged("更新后的第二条公告"));

        RunOnboardingScenario(logger, Path.Combine(testDirectory, "welcome-none"), (window, _) => {
            Require(OverlayOf(window) == Visibility.Collapsed, "没有公告的项目不得弹窗。");
            ClickOnboarding(window, "SettingsNavigationItem");
            PumpOnboarding();
            var view = SettingsActionFor(window, "welcome.view");
            Require(!SettingsVisualDescendants(Named<SettingsView>(window, "SettingsView"))
                .OfType<System.Windows.Controls.Button>().Any(button => button.Command == view && button.IsVisible),
                "没有公告时设置页不得显示查看入口。");
        });

        RunOnboardingScenario(logger, Path.Combine(testDirectory, "welcome-long"), (window, _) => {
            Require(OverlayOf(window) == Visibility.Visible, "长公告必须弹出。");
            foreach (var (width, height) in new[] { (1180.0, 760.0), (920.0, 640.0) }) {
                window.Width = width;
                window.Height = height;
                PumpOnboarding(300);
                var overlay = Named<FrameworkElement>(window, "WelcomeOverlay");
                var dialog = Named<FrameworkElement>(window, "WelcomeDialog");
                var dismiss = Named<FrameworkElement>(window, "WelcomeDismissButton");
                var bounds = dismiss.TransformToAncestor(overlay).TransformBounds(new Rect(dismiss.RenderSize));
                Require(dialog.ActualHeight <= overlay.ActualHeight * 0.8 + PixelTolerance,
                    $"长公告不得超过窗口高度的 80%：{width}x{height}。");
                Require(bounds.Top >= 0 && bounds.Bottom <= overlay.ActualHeight + PixelTolerance && dismiss.IsVisible,
                    $"长公告时“我知道了”必须完整可见：{width}x{height}。");
                CaptureWelcome(window, $"long-{width}");
                var viewer = Named<FlowDocumentScrollViewer>(window, "WelcomeViewer");
                Require(SettingsVisualDescendants(viewer).OfType<ScrollViewer>().Single() is { ScrollableHeight: > 0 },
                    $"长公告正文必须可以滚动：{width}x{height}。");
            }
        }, beforeLoad: directory => SetWelcome(directory, longNotice, "项目标题"));

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUNSHIN_SUPPORT_QA_DIR"))) {
            return;
        }
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "welcome-sample"), (window, _) => {
            PumpOnboarding(400);
            CaptureWelcome(window, "sample");
        }, beforeLoad: directory => SetWelcome(directory, new JsonArray(
            "# 欢迎使用 MaaNOP\n\n第一次运行前，请先看看这几点：\n\n- 请把「进入游戏」放在其他任务之前；使用快捷登录时，QQ 须已在电脑上登录。\n"
            + "- 「注册并练级」会把角色练到 **16 级**，请勿提前注册。\n- 托管前，请先在游戏里勾选要做的项目；每天首次托管消耗 **1 张托管卡**。",
            "## 购物节送字须知\n\n依次登录所选服务器，在木叶购物节消费 **1500 点券**，再将获得的字赠送给指定大号。\n\n"
            + "- 大号须保持在线，并已被各小号加为好友。\n- 每个小号每天只运行一次，重复运行会再次购买。"), "MaaNOP"));
    }

    private static void CaptureWelcome(MainWindow window, string suffix)
    {
        if (Environment.GetEnvironmentVariable("BUNSHIN_SUPPORT_QA_DIR") is { Length: > 0 } output) {
            Directory.CreateDirectory(output);
            PumpOnboarding(300);
            SaveScreenshot(window, Path.Combine(output, $"welcome-{suffix}.png"));
        }
    }
}
