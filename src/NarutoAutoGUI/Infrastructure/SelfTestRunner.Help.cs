using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NarutoAutoGUI.Views;
using WpfPoint = System.Windows.Point;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    // Help sits between Update and Settings, each answer opens on its own, and the page exports diagnostics through
    // the same action as Settings.
    private static void VerifyHelpPage(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "help-page"), (window, _) => {
            double Top(string name) => ((UIElement)window.FindName(name)).TranslatePoint(new WpfPoint(), window).Y;
            Require(Top("UpdateNavigationItem") < Top("HelpNavigationItem")
                && Top("HelpNavigationItem") < Top("SettingsNavigationItem"), "帮助应位于更新与设置之间。");
            ClickOnboarding(window, "HelpNavigationItem");
            var view = (HelpView)window.FindName("HelpView");
            Require(((RailItem)window.FindName("HelpNavigationItem")).IsActive && view.IsVisible
                && !((RailItem)window.FindName("HomeNavigationItem")).IsActive
                && !((UIElement)window.FindName("HomeView")).IsVisible
                && !((UIElement)window.FindName("RuntimeSidebar")).IsVisible, "点击帮助应切换到常见问题页并标记选中项。");
            var page = (HelpPageModel)view.DataContext;
            Require(page.Sections.Sum(section => section.Questions.Count) == 3
                && ReferenceEquals(page.ExportDiagnostics, SettingsActionFor(window, "diagnostics.export")),
                "常见问题页应包含三个问题并复用诊断包导出。");

            var headers = SettingsVisualDescendants(view).OfType<ToggleButton>().ToArray();
            var answers = SettingsVisualDescendants(view).OfType<RevealDecorator>().ToArray();
            Require(headers.Length == 3 && answers.All(answer => answer.ActualHeight == 0), "问题默认应全部收起。");
            Capture("collapsed");
            headers[1].IsChecked = true;
            PumpOnboarding(400);
            Require(page.Sections[0].Questions[1].IsExpanded && answers[1].ActualHeight > 100
                && answers[0].ActualHeight == 0 && answers[2].ActualHeight == 0, "展开一个问题应只显示它的答案。");
            headers[2].IsChecked = true;
            PumpOnboarding(400);
            Require(answers[1].ActualHeight > 100 && answers[2].ActualHeight > 50, "多个问题应可同时展开。");
            Capture("expanded");
            SettingsVisualDescendants(view).OfType<ScrollViewer>().First().ScrollToEnd();
            PumpOnboarding();
            Capture("bottom");
            headers[1].IsChecked = false;
            PumpOnboarding(400);
            Require(!page.Sections[0].Questions[1].IsExpanded && answers[1].ActualHeight == 0
                && answers[2].ActualHeight > 50, "收起一个问题不应影响其他问题。");

            void Capture(string suffix)
            {
                var output = Environment.GetEnvironmentVariable("NARUTO_SUPPORT_QA_DIR");
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
        Console.WriteLine("HELP SELF-TEST PASS: rail placement, page switch, independent answers, shared export.");
    }
}
