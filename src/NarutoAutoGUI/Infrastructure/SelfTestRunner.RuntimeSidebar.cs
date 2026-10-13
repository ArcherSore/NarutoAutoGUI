using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NarutoAutoGUI.Views;
using Point = System.Windows.Point;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyRuntimeSidebarResize(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "sidebar-resize"), (window, _) => {
            typeof(MainWindow).GetMethod("EndOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { true });
            var home = Named<Grid>(window, "HomeView");
            var column = Named<ColumnDefinition>(window, "RuntimeSidebarColumn");
            var splitter = Named<GridSplitter>(window, "RuntimeSidebarSplitter");
            var console = Named<FrameworkElement>(window, "RuntimeSidebar");
            var control = Named<FrameworkElement>(window, "RuntimeControlCard");
            var preview = Named<FrameworkElement>(window, "PreviewScreen");
            var log = Named<FrameworkElement>(window, "HomeLogListBox");
            var controlTop = control.TransformToAncestor(home).Transform(new Point()).Y;
            var controlHeight = control.ActualHeight;
            Require(!splitter.ShowsPreview && splitter.Cursor == System.Windows.Input.Cursors.SizeWE
                && splitter.ActualWidth >= 10, "分隔器必须有容易命中的区域、水平调整指针，并实时更新布局。");
            var hit = splitter.TransformToAncestor(home).Transform(new Point(6, splitter.ActualHeight / 2));
            Require(home.InputHitTest(hit) is DependencyObject target
                && (target == splitter || splitter.IsAncestorOf(target)), "任务区右缘应命中拖拽区域。");
            Require(Math.Abs(splitter.ActualWidth - Named<FrameworkElement>(window, "TasksView").Margin.Right) < 0.5
                && Math.Abs(splitter.TransformToAncestor(home).Transform(new Point(splitter.ActualWidth, 0)).X
                    - console.TransformToAncestor(home).Transform(new Point()).X) < 0.5,
                "拖拽区域应正好覆盖任务区与侧栏之间的间距。");
            // The shown handle straddles the console border: its inner half drags, but hidden it covers nothing.
            var handle = (FrameworkElement)splitter.Template.FindName("Handle", splitter);
            bool HandleInnerHalfDrags()
            {
                var point = handle.TransformToAncestor(home).Transform(new Point(handle.ActualWidth * 0.75, 10));
                return console.TransformToAncestor(home).Transform(new Point()).X < point.X
                    && home.InputHitTest(point) is DependencyObject hit
                    && (hit == splitter || splitter.IsAncestorOf(hit));
            }
            Require(!HandleInnerHalfDrags(), "隐藏的手柄不应遮挡侧栏左缘的控件。");
            VisualStateManager.GoToState(splitter, "Hover", false);
            PumpOnboarding(300);
            Require(HandleInnerHalfDrags(), "显示的手柄覆盖侧栏的一半也应可以拖动。");
            VisualStateManager.GoToState(splitter, "Hidden", false);
            PumpOnboarding(300);
            Require(!HandleInnerHalfDrags(), "手柄隐藏后应立即不再遮挡侧栏。");

            var originalWidth = console.ActualWidth;
            var originalPreviewHeight = preview.ActualHeight;
            var originalLogHeight = log.ActualHeight;
            StartDrag();
            MoveDrag(20);
            Require(Math.Abs(console.ActualWidth - (originalWidth - 20)) < 1
                && preview.ActualHeight < originalPreviewHeight && log.ActualHeight > originalLogHeight,
                "拖动尚未结束时，侧栏变窄应实时缩小 Preview 并增加日志高度。");
            MoveDrag(-40);
            Require(Math.Abs(console.ActualWidth - (originalWidth + 20)) < 1
                && preview.ActualHeight > originalPreviewHeight && log.ActualHeight < originalLogHeight,
                "反向拖动应实时放大 Preview 并减少日志高度。");
            EndDrag();
            AssertLayout();

            // Window resizing must clamp the displayed width without discarding the user's wider preference.
            Drag(-10000);
            Require(Math.Abs(console.ActualWidth - 540) < 1, "右侧栏应有最大宽度边界。");
            window.Width = 920;
            window.Height = 640;
            PumpOnboarding();
            Require(home.ColumnDefinitions[0].ActualWidth >= 419.5 && console.ActualWidth < 540,
                $"窄窗口应限制侧栏宽度并保留任务区最小宽度：任务区={home.ColumnDefinitions[0].ActualWidth}，"
                + $"侧栏={console.ActualWidth}，主页={home.ActualWidth}，上界={column.MaxWidth}，窗口={window.ActualWidth}。");
            AssertLayout();
            ClickOnboarding(window, "TaskShelfHeaderButton");
            var badge = Named<FrameworkElement>(window, "AddedTasksBadge");
            Named<TextBlock>(window, "AddedTasksText").Text = "已添加 1 项";
            badge.Visibility = Visibility.Visible;
            PumpOnboarding();
            var shelf = Named<FrameworkElement>(window, "TaskShelfContent");
            var search = Named<FrameworkElement>(window, "TaskSearchBox");
            var badgeBounds = badge.TransformToAncestor(shelf).TransformBounds(new Rect(badge.RenderSize));
            var searchBounds = search.TransformToAncestor(shelf).TransformBounds(new Rect(search.RenderSize));
            Require(badgeBounds.Right <= searchBounds.Left + 0.5 && searchBounds.Right <= shelf.ActualWidth,
                "最窄任务区中可用任务标题、新增提示和搜索框不能重叠或溢出。");
            ClickOnboarding(window, "TaskShelfHeaderButton");
            ClickOnboarding(window, "SettingsNavigationItem");
            Require(column.Width.Value == 0 && splitter.Visibility == Visibility.Collapsed,
                "设置页应隐藏侧栏与分隔器。");
            window.Width = 1440;
            PumpOnboarding();
            ClickOnboarding(window, "HomeNavigationItem");
            Require(Math.Abs(console.ActualWidth - 540) < 1, "返回首页且空间恢复后应恢复用户宽度。");

            Drag(10000);
            Require(Math.Abs(console.ActualWidth - 340) < 1, "右侧栏应有最小宽度边界。");
            AssertLayout();
            Drag(-80);
            var preferred = console.ActualWidth;
            foreach (var width in new[] { 920, 1180, 1440 }) {
                window.Width = width;
                window.Height = 640;
                PumpOnboarding();
                ClickOnboarding(window, "SettingsNavigationItem");
                ClickOnboarding(window, "HomeNavigationItem");
                Require(Math.Abs(console.ActualWidth - preferred) < 1,
                    "窗口缩放和首页/设置切换不应覆盖仍可容纳的用户宽度。");
                AssertLayout();
            }

            KeyDown(System.Windows.Input.Key.Left);
            Require(Math.Abs(console.ActualWidth - (preferred + splitter.KeyboardIncrement)) < 1,
                "原生方向键应可调整侧栏宽度。");
            preferred = console.ActualWidth;
            StartDrag();
            MoveDrag(30);
            KeyDown(System.Windows.Input.Key.Escape);
            EndDrag();
            ClickOnboarding(window, "SettingsNavigationItem");
            ClickOnboarding(window, "HomeNavigationItem");
            Require(Math.Abs(console.ActualWidth - preferred) < 1, "取消拖拽后应保留原宽度。");

            var expandedLogHeight = log.ActualHeight;
            typeof(MainWindow).GetMethod("SetPreviewExpanded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { false });
            PumpOnboarding();
            Require(log.ActualHeight > expandedLogHeight + 100, "收起 Preview 后日志应填充剩余高度。");
            Drag(20);
            typeof(MainWindow).GetMethod("SetPreviewExpanded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { true });
            PumpOnboarding();
            AssertLayout();
            // Open through the real handler; no game or preview capture is needed for overlay layout.
            ClickOnboarding(window, "ExpandPreviewButton");
            Require(Named<UIElement>(window, "PreviewOverlay").Visibility == Visibility.Visible
                && !Named<UIElement>(window, "MainNavigation").IsEnabled, "放大预览仍应打开模态画面。");
            ClickOnboarding(window, "ClosePreviewButton");
            Require(Named<UIElement>(window, "PreviewOverlay").Visibility == Visibility.Collapsed,
                "放大预览应可以正常关闭。");
            AssertLayout();

            void StartDrag() => splitter.RaiseEvent(new DragStartedEventArgs(0, 0) {
                RoutedEvent = Thumb.DragStartedEvent
            });

            void MoveDrag(double change)
            {
                splitter.RaiseEvent(new DragDeltaEventArgs(change, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                PumpOnboarding();
            }

            void EndDrag() => splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false) {
                RoutedEvent = Thumb.DragCompletedEvent
            });

            void Drag(double change)
            {
                StartDrag();
                MoveDrag(change);
                EndDrag();
            }

            void KeyDown(System.Windows.Input.Key key)
            {
                splitter.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                PumpOnboarding();
            }

            void AssertLayout()
            {
                Require(Math.Abs(preview.ActualHeight - preview.ActualWidth * 9 / 16) < 1,
                    "调整宽度后 Preview 必须保持 16:9。");
                Require(Math.Abs(control.TransformToAncestor(home).Transform(new Point()).Y - controlTop) < 1
                    && Math.Abs(control.ActualHeight - controlHeight) < 1, "运行控制顶部位置和布局应保持稳定。");
                Require(log.ActualHeight > 0 && log.TransformToAncestor(console)
                    .Transform(new Point(log.ActualWidth, log.ActualHeight)).Y <= console.ActualHeight + 0.5,
                    $"日志应填充剩余高度且不溢出侧栏：日志高度={log.ActualHeight}，侧栏高度={console.ActualHeight}。");
                Require(home.ColumnDefinitions[0].ActualWidth >= 419.5
                    && home.ActualWidth <= LayoutInformation.GetLayoutSlot(home).Width + 0.5
                    && console.TransformToAncestor(home).Transform(new Point(console.ActualWidth, 0)).X
                        <= home.ActualWidth + 0.5, "左右栏必须处于主页边界内。");
            }
        });
        Console.WriteLine("RUNTIME SIDEBAR RESIZE PASS: native drag/key events, bounds, 16:9, log space, navigation.");
    }
}
