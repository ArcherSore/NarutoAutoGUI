using System.Reflection;
using System.Windows;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    // The rail keeps one fixed width with no expand toggle, and a navigation preference left by an older version is
    // simply ignored.
    private static void VerifyNavigationRail(AppLogger logger, string testDirectory)
    {
        string? preference = null;
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "navigation-rail"), (window, directory) => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var rail = (FrameworkElement)window.FindName("MainNavigation");
            Require(rail.ActualWidth == 64 && window.FindName("PART_ToggleButton") is null
                && File.ReadAllText(preference!) == "false", "导航栏应固定为 64 像素、没有展开入口，并忽略旧偏好。");
            var home = (RailItem)window.FindName("HomeNavigationItem");
            var settings = (RailItem)window.FindName("SettingsNavigationItem");
            ClickOnboarding(window, "SettingsNavigationItem");
            Require(settings.IsActive && !home.IsActive && ((UIElement)window.FindName("SettingsView")).IsVisible
                && !((UIElement)window.FindName("HomeView")).IsVisible, "点击设置应切换到设置页并标记选中项。");
            ClickOnboarding(window, "HomeNavigationItem");
            Require(home.IsActive && !settings.IsActive && ((UIElement)window.FindName("HomeView")).IsVisible,
                "点击首页应回到首页。");
            var state = typeof(MainWindow).GetField("_updateCheckState", flags)!;
            var states = state.FieldType;
            foreach (var (name, badge, loading) in new[] {
                ("UpdateAvailable", true, false), ("Checking", false, true), ("UpToDate", false, false)
            }) {
                state.SetValue(window, Enum.Parse(states, name));
                InvokeOnboarding(window, "RefreshUpdatePresentation");
                Require(((UIElement)window.FindName("UpdateNavigationBadge")).IsVisible == badge
                    && ((UIElement)window.FindName("UpdateNavigationLoading")).IsVisible == loading,
                    $"更新状态 {name} 的提示点或加载指示不符。");
            }
        }, directory => {
            preference = Path.Combine(directory, "config", "navigation-pane.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(preference)!);
            File.WriteAllText(preference, "false");
        });
        Console.WriteLine("NAVIGATION SELF-TEST PASS: fixed rail, sections, update indicators, legacy preference.");
    }
}
