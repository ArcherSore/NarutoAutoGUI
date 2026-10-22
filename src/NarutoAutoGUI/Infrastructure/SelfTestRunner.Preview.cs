using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using NarutoAutoGUI.ChildSession;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfSize = System.Windows.Size;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyRdpViewingModes()
    {
        var window = new RdpPreviewWindow();
        try {
            window.Show();
            PumpOnboarding(50);
            T Element<T>(string name) => (T)window.FindName(name);
            void InvokeHost(string name, params object?[] arguments) => typeof(RdpActiveXHost)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window.Host, arguments);
            void Click(WpfButton button) => button.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
            var status = Element<WpfTextBlock>("StatusText");
            var resize = Element<WpfButton>("SizeButton");
            var resizeText = Element<WpfTextBlock>("SizeText");
            var input = Element<WpfButton>("InputButton");
            var inputText = Element<WpfTextBlock>("InputText");
            var interactiveChanges = 0;
            window.InteractiveChanged += (_, _) => interactiveChanges++;
            if (!status.Text.StartsWith("正在连接", StringComparison.Ordinal)) {
                throw new InvalidOperationException("认证阶段必须提示完成登录，不能声称已锁定输入。");
            }
            // RDP keeps keyboard focus in a child input window while credentials are typed. Disabling the host does
            // not clear a child's focus, so a visible native child (WS_CHILD | WS_VISIBLE) stands in for it.
            var host = window.Host.Handle;
            var inputWindow = new Forms.NativeWindow();
            inputWindow.CreateHandle(new Forms.CreateParams { ClassName = "EDIT", Parent = host, Style = 0x50000000 });
            SetFocus(inputWindow.Handle);
            if (!window.Host.ContainsFocus) {
                throw new InvalidOperationException("测试前提：认证阶段 RDP 子窗口应持有焦点。");
            }
            // Exercise native HWND disabling without connecting or creating a Child Session.
            InvokeHost("OnLoginComplete");
            void AssertLocked(string stage)
            {
                PumpOnboarding(50);
                if (IsWindowEnabled(host) || window.Host.TabStop || window.Host.ContainsFocus
                    || inputText.Text != "只读" || input.Tag is not null || window.Interactive) {
                    throw new InvalidOperationException($"{stage} 必须在原生 RDP HWND 阻止输入，焦点不得留在 RDP 窗口。");
                }
            }
            // Relocking is only meaningful after input was really unlocked.
            // Unlocking also returns focus to the RDP child, so every later lock must really move it out.
            void Unlock(string stage)
            {
                var changes = interactiveChanges;
                Click(input);
                if (!window.Interactive || interactiveChanges != changes + 1) {
                    throw new InvalidOperationException($"{stage} 解锁必须通知主窗口一次，供分身按钮显示“可操作”。");
                }
                SetFocus(inputWindow.Handle);
                if (!IsWindowEnabled(host) || !window.Host.TabStop || inputText.Text != "可操作"
                    || !window.Host.ContainsFocus) {
                    throw new InvalidOperationException($"{stage} 手动解锁后必须允许输入、显示可操作并可聚焦 RDP 窗口。");
                }
            }
            // WPF may keep a size it already snapped to device pixels, so sizes match within a pixel.
            void AssertMode(string stage, WpfSize size, bool large)
            {
                PumpOnboarding(50);
                if (window.WindowState != WindowState.Normal || Math.Abs(window.Width - size.Width) > 1
                    || Math.Abs(window.Height - size.Height) > 1
                    || window.Topmost == large || resizeText.Text != (large ? "小窗" : "放大")
                    || input.IsEnabled != large) {
                    throw new InvalidOperationException($"{stage} 的窗口模式、尺寸、置顶或输入按钮状态不符："
                        + $"{window.WindowState} {window.Width}x{window.Height}（应为 {size}），置顶={window.Topmost}。");
                }
            }
            AssertLocked("登录后小窗");
            var smallSize = new WpfSize(window.Width, window.Height);
            AssertMode("登录后小窗", smallSize, large: false);
            if (status.Text.Length != 0) {
                throw new InvalidOperationException("连接后标题栏不应保留连接提示。");
            }
            window.SetViewingMode(large: true);
            var largeSize = new WpfSize(window.Width, window.Height);
            AssertLocked("放大后");
            Unlock("大窗");
            // Prepare completion shows the window; a visible window keeps the user's choice.
            window.ShowViewing();
            if (!IsWindowEnabled(host)) {
                throw new InvalidOperationException("显示可见窗口不得锁定用户已解锁的大窗。");
            }
            AssertMode("保持大窗", largeSize, large: true);
            window.SetViewingMode(large: false);
            AssertLocked("返回小窗");
            window.SetViewingMode(large: true);
            Unlock("隐藏前");
            window.HideViewing();
            AssertLocked("隐藏后");
            window.ShowViewing();
            AssertLocked("重新显示");
            AssertMode("隐藏后重新显示", smallSize, large: false);

            // Show restores stale native bounds; reopening must still return to the small mode.
            window.WindowState = WindowState.Maximized;
            window.HideViewing();
            window.ShowViewing();
            AssertLocked("最大化后隐藏并显示小窗");
            AssertMode("最大化后隐藏并显示小窗", smallSize, large: false);
            window.WindowState = WindowState.Minimized;
            window.HideViewing();
            window.ShowViewing();
            AssertLocked("最小化后隐藏并重新显示");
            AssertMode("最小化后隐藏并重新显示", smallSize, large: false);

            window.WindowState = WindowState.Maximized;
            PumpOnboarding(50);
            if (resizeText.Text != "小窗" || !input.IsEnabled) {
                throw new InvalidOperationException("标题栏最大化必须同步大窗状态和返回小窗按钮。");
            }
            Click(resize);
            AssertLocked("最大化返回小窗");
            window.WindowState = WindowState.Maximized;
            Unlock("最大化");
            window.WindowState = WindowState.Normal;
            AssertLocked("标题栏还原到小窗");
            AssertMode("标题栏还原到小窗", smallSize, large: false);
            window.SetViewingMode(large: true);
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Normal;
            AssertLocked("标题栏还原到大窗");
            AssertMode("标题栏还原到大窗", largeSize, large: true);
            if (window.Host.Handle != host) {
                throw new InvalidOperationException("切换观看模式不得重建 RDP HWND。");
            }
            // SmartSizing stretches the desktop to the host, so the host keeps 16:9 whatever the window shape.
            window.Width = largeSize.Width * 0.6;
            PumpOnboarding(50);
            var picture = Element<FrameworkElement>("RdpFormsHost");
            if (Math.Abs(picture.ActualWidth * 9 / 16 - picture.ActualHeight) > 1) {
                throw new InvalidOperationException("任意窗口比例下 RDP 画面都必须保持 16:9。");
            }

            Unlock("断开前");
            InvokeHost("ReportConnectionFailure", "RDP 连接意外断开。", 0, null);
            AssertLocked("意外断开");
            if (!status.Text.StartsWith("连接已断开", StringComparison.Ordinal) || resize.IsEnabled || input.IsEnabled) {
                throw new InvalidOperationException("意外断开后必须锁定输入、停用切换并说明连接已断开。");
            }
        } finally {
            window.Close();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
}
