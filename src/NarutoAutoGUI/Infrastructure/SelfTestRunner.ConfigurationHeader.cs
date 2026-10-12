using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Panel = System.Windows.Controls.Panel;
using TabControl = System.Windows.Controls.TabControl;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyConfigurationHeader(AppLogger logger, string testDirectory)
    {
        Require(MainWindow.RevealOffset(0, 300, 400, 100, 32) == 232
            && MainWindow.RevealOffset(300, 300, 0, 100, 32) == 0
            && MainWindow.RevealOffset(100, 300, 200, 100, 32) == 100,
            "选中标签滚入视野时应带边距且尽量少移动。");
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "configuration-header"), (window, _) => {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var project = (ProjectPlanModule)typeof(MainWindow).GetField("_projectPlan", flags)!.GetValue(window)!;
            typeof(MainWindow).GetMethod("EndOnboarding", flags)!.Invoke(window, new object[] { true });
            for (var index = 0; index < 13; index++) {
                project.CreateConfiguration();
            }
            project.ActivateConfiguration(project.Configurations[0].Id);
            InvokeOnboarding(window, "RenderTaskPlan");
            var tabs = (TabControl)window.FindName("ConfigurationTabs");
            var bar = (FrameworkElement)window.FindName("ConfigurationBar");
            var badge = (FrameworkElement)window.FindName("ConfigurationLockBadge");
            var add = (FrameworkElement)window.FindName("NewConfigurationButton");
            var scroll = (ScrollViewer)tabs.Template.FindName("ConfigurationScroll", tabs);
            var header = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(bar);
            var title = ((Panel)header).Children.OfType<TextBlock>().Single(text => text.Text == "任务");
            void SetBusy(bool busy)
            {
                SetOnboardingField(window, "_pendingOperation",
                    busy ? PendingOperation.PreparingEnvironment : PendingOperation.None);
                InvokeOnboarding(window, "RefreshRuntimeControls");
                PumpOnboarding();
            }
            bool Inside(FrameworkElement element, FrameworkElement container)
            {
                var bounds = element.TransformToAncestor(container)
                    .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return bounds.Left >= -0.5 && bounds.Right <= container.ActualWidth + 0.5;
            }
            bool Fade(string name) =>
                ((UIElement)scroll.Template.FindName(name, scroll)).Visibility == Visibility.Visible;

            SetBusy(true);
            foreach (var width in new[] { 920, 1180, 1440 }) {
                window.Width = width;
                PumpOnboarding();
                Require(header.ActualHeight == 36 && bar.ActualHeight == 34
                    && title.ActualWidth >= title.DesiredSize.Width - 0.5 && badge.IsVisible && Inside(badge, header)
                    && badge.ActualWidth >= badge.DesiredSize.Width - 0.5 && Inside(add, bar)
                    && scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible && scroll.ScrollableWidth > 0,
                    $"{width} 宽度下 14 个配置锁定时，页头应为单行，标题、锁定徽标与新建按钮完整可见。");
            }
            Require(((TextBlock)window.FindName("ConfigurationLockText")).Text != "已锁定", "宽于 1180 时锁定徽标显示完整说明。");
            window.Width = 920;
            PumpOnboarding();
            Require(((TextBlock)window.FindName("ConfigurationLockText")).Text == "已锁定"
                && badge.ToolTip is string reason && reason.Contains("锁定", StringComparison.Ordinal),
                "窄于 1180 时锁定徽标改为紧凑形式，完整原因保留在提示中。");

            scroll.ScrollToHorizontalOffset(0);
            PumpOnboarding();
            Require(!Fade("LeftFade") && Fade("RightFade"), "只有右侧还有内容时才显示右侧渐隐。");
            void Wheel()
            {
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent
                });
                PumpOnboarding();
            }
            Wheel();
            Require(Math.Abs(scroll.HorizontalOffset - 64) < 0.5 && Fade("LeftFade"), "锁定时垂直滚轮仍应横向滚动标签。");
            var other = tabs.Items.OfType<ConfigurationTab>().Last();
            Require(other.ToolTip is string blocked && blocked.Contains("无法切换配置", StringComparison.Ordinal),
                "锁定时非当前标签应说明无法切换。");
            other.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, other) {
                RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent
            });
            Require(!other.IsSelected && project.ActiveConfigurationId == project.Configurations[0].Id
                && other.Background == System.Windows.Media.Brushes.Transparent
                && tabs.Items.OfType<TabItem>().First().ToolTip is string active
                && active.Contains("暂时不能重命名", StringComparison.Ordinal),
                "锁定时焦点移到其他标签不得切换配置，被阻止的标签不显示悬停底色，当前标签说明不能重命名。");

            SetBusy(false);
            scroll.ScrollToHorizontalOffset(0);
            PumpOnboarding();
            Wheel();
            Require(Math.Abs(scroll.HorizontalOffset - 64) < 0.5, "可编辑时垂直滚轮横向滚动标签。");
            other.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, other) {
                RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent
            });
            PumpOnboarding();
            var last = tabs.Items.OfType<TabItem>().Last();
            Require(project.ActiveConfigurationId == project.Configurations[^1].Id && last.IsSelected
                && Inside(last, scroll) && scroll.HorizontalOffset >= scroll.ScrollableWidth - 0.5 && Inside(add, bar)
                && !Fade("RightFade"), "可编辑时焦点切换选择，最后一个标签带边距完整显示。");
            project.ActivateConfiguration(project.Configurations[0].Id);
            InvokeOnboarding(window, "RenderTaskPlan");
            PumpOnboarding();
            Require(scroll.HorizontalOffset == 0 && Inside(tabs.Items.OfType<TabItem>().First(), scroll),
                "第一个标签被选中时应完整显示。");

            typeof(MainWindow).GetMethod("ShowProjectValidationError", flags)!
                .Invoke(window, new object[] { new InvalidDataException("配置校验失败") });
            tabs.SelectedIndex = 1;
            Require(bar.IsVisible && project.ActiveConfigurationId == project.Configurations[1].Id,
                "任务配置校验失败时标签条保持显示，并能切换配置。");
            var drafts = (System.Collections.IDictionary)typeof(MainWindow).GetField("_invalidInputDrafts", flags)!
                .GetValue(window)!;
            drafts[(project.ActiveConfigurationId, "ServerRange", "server_range")] = new InputDraft("978-,", "格式错误");
            InvokeOnboarding(window, "RenderTaskPlan");
            tabs.SelectedIndex = 0;
            Require(bar.IsVisible && project.ActiveConfigurationId == project.Configurations[0].Id,
                "参数存在无效草稿时标签条保持显示，并能切换配置。");
            typeof(MainWindow).GetMethod("ShowProjectUnavailableState", flags)!
                .Invoke(window, new object[] { "MaaNOP 项目无法加载", "detail" });
            PumpOnboarding();
            Require(!bar.IsVisible && title.IsVisible, "项目无法加载时标签条隐藏，标题保留。");

            Require(typeof(MainWindow).GetField("_configurationWheelSource", flags)!.GetValue(window) is not null,
                "显示窗口后应安装横向滚轮钩子。");
            var hookArguments = new object[] { IntPtr.Zero, 0x020E, new IntPtr(120 << 16), IntPtr.Zero, false };
            typeof(MainWindow).GetMethod("ConfigurationWheelHook", flags)!.Invoke(window, hookArguments);
            Require(!(bool)hookArguments[4], "横向滚轮只在指针位于标签条上时处理。");
            window.AllowClose();
            typeof(MainWindow).GetMethod("MainWindow_Closing", flags)!
                .Invoke(window, new object?[] { window, new System.ComponentModel.CancelEventArgs() });
            Require(typeof(MainWindow).GetField("_configurationWheelSource", flags)!.GetValue(window) is null,
                "窗口关闭时必须移除横向滚轮钩子。");
        });
    }
}
