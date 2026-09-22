using System.Windows;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Wpf.Ui.Controls;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyNavigationPreference(AppLogger logger, string testDirectory)
    {
        var scenario = Path.Combine(testDirectory, "navigation-preference");
        RunOnboardingScenario(logger, scenario, (window, directory) => {
            var navigation = (NavigationView)window.FindName("MainNavigation");
            var path = Path.Combine(directory, "config", "navigation-pane.txt");
            if (!navigation.IsPaneOpen || File.Exists(path)) {
                throw new InvalidOperationException("侧栏首次加载应默认展开且不写入偏好。");
            }
            Toggle(navigation);
            if (navigation.IsPaneOpen || File.ReadAllText(path) != "false") {
                throw new InvalidOperationException("点击菜单折叠侧栏必须保存偏好。");
            }
            window.Hide();
            window.Show();
            PumpOnboarding();
            if (navigation.IsPaneOpen) {
                throw new InvalidOperationException("隐藏后重新显示不能重置侧栏状态。");
            }
        });
        RunOnboardingScenario(logger, scenario, (window, directory) => {
            var navigation = (NavigationView)window.FindName("MainNavigation");
            var path = Path.Combine(directory, "config", "navigation-pane.txt");
            if (navigation.IsPaneOpen || File.ReadAllText(path) != "false") {
                throw new InvalidOperationException("重新创建窗口必须恢复折叠状态。");
            }
            Toggle(navigation);
            if (!navigation.IsPaneOpen || File.ReadAllText(path) != "true") {
                throw new InvalidOperationException("重新展开侧栏必须保存偏好。");
            }
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Toggle(navigation);
            if (navigation.IsPaneOpen || File.ReadAllText(path) != "true") {
                throw new InvalidOperationException("偏好写入失败不能阻止侧栏切换或损坏旧偏好。");
            }
        });
        RunOnboardingScenario(logger, scenario, (window, directory) => {
            if (!((NavigationView)window.FindName("MainNavigation")).IsPaneOpen) {
                throw new InvalidOperationException("重新创建窗口必须恢复展开状态。");
            }
        });
        Console.WriteLine("NAVIGATION SELF-TEST PASS: default, menu toggle, reopen, hide/show and write failure.");

        static void Toggle(NavigationView navigation)
        {
            var button = (UIElement)navigation.Template.FindName("PART_ToggleButton", navigation);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpOnboarding();
        }
    }
}
