using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Gui.ChildSession;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    // A user whose Windows only allows Hello sign-in must be told so when preparing fails, and nobody else misled.
    private static void VerifyPreparationGuidance()
    {
        const string prepare = "准备运行环境失败";
        var failure = new ChildSessionConnectionException("RDP 原始错误，断开码=123，扩展码=456");
        var guidance = MainWindow.GetWindowsHelloGuidance(prepare, failure, true);
        Require(!string.IsNullOrWhiteSpace(guidance) && !guidance.Contains(failure.Message),
            "Hello 场景应直接给出恢复指引，不混入原始 RDP 错误和断开码。");
        Require(MainWindow.GetWindowsHelloGuidance(prepare, failure, false) is null,
            "未开启 Hello 限制时应保留原始故障说明。");
        Require(MainWindow.GetWindowsHelloGuidance(prepare,
            new InvalidOperationException("Worker 启动失败"), true) is null,
            "即使开启 Hello 限制，也不能把 Worker 等其他准备失败归因于分身登录。");
        Require(MainWindow.GetWindowsHelloGuidance(prepare, new InvalidOperationException("包装", failure), true)
            == guidance, "包装的 RDP 连接异常也应保留恢复指引。");
        const string start = "开始任务失败";
        Require(MainWindow.GetWindowsHelloGuidance(start, failure, true) is null,
            "Windows Hello 指引不应出现在与登录无关的操作上。");
    }

    // Help sits between Update and Settings, each answer opens on its own, and the page exports diagnostics through
    // the same action as Settings.
    private static void VerifyHelpPage(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "help-page"), (window, _) => {

            ClickOnboarding(window, "HelpNavigationItem");
            var view = (HelpView)window.FindName("HelpView");
            Require(((RailItem)window.FindName("HelpNavigationItem")).IsActive && view.IsVisible
                && !((RailItem)window.FindName("HomeNavigationItem")).IsActive
                && !((UIElement)window.FindName("HomeView")).IsVisible
                && !((UIElement)window.FindName("RuntimeSidebar")).IsVisible, "点击帮助应切换到常见问题页并标记选中项。");
            var page = (HelpPageModel)view.DataContext;
            Require(ReferenceEquals(page.ExportDiagnostics, SettingsActionFor(window, "diagnostics.export")),
                "常见问题页应复用诊断包导出。");

            var headers = SettingsVisualDescendants(view).OfType<ToggleButton>().ToArray();
            var answers = SettingsVisualDescendants(view).OfType<RevealDecorator>().ToArray();
            Require(headers.Length >= 3 && answers.All(answer => answer.ActualHeight == 0), "问题默认应全部收起。");
            Capture("collapsed");
            headers[1].IsChecked = true;
            PumpUntil(() => answers[1].ActualHeight > 0, "展开一个问题应显示它的答案。");
            Require(page.Sections[0].Questions[1].IsExpanded && answers[0].ActualHeight == 0
                && answers[2].ActualHeight == 0, "展开一个问题不应显示其他答案。");
            headers[2].IsChecked = true;
            PumpUntil(() => answers[2].ActualHeight > 0 && answers[1].ActualHeight > 0, "多个问题应可同时展开。");
            Capture("expanded");
            SettingsVisualDescendants(view).OfType<ScrollViewer>().First().ScrollToEnd();
            PumpOnboarding();
            Capture("bottom");
            headers[1].IsChecked = false;
            PumpUntil(() => answers[1].ActualHeight == 0, "收起问题后应隐藏它的答案。");
            Require(!page.Sections[0].Questions[1].IsExpanded && answers[2].ActualHeight > 0,
                "收起一个问题不应影响其他问题。");

            void Capture(string suffix)
            {
                var output = Environment.GetEnvironmentVariable("BUNSHIN_SUPPORT_QA_DIR");
                if (string.IsNullOrWhiteSpace(output)) {
                    return;
                }
                Directory.CreateDirectory(output);
                foreach (var (width, height) in new[] { (1180.0, 760.0), (920.0, 640.0) }) {
                    window.Width = width;
                    window.Height = height;
                    PumpOnboarding(300);
                    SaveScreenshot(window, Path.Combine(output, $"help-{suffix}-{width}.png"));
                }
                window.Width = 1180;
                window.Height = 760;
                PumpOnboarding();
            }
        });
    }
}
