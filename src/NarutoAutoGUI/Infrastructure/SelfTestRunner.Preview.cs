using System.Reflection;
using System.Runtime.InteropServices;
using NarutoAutoGUI.ChildSession;
using Forms = System.Windows.Forms;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyRdpViewingModes()
    {
        using var form = new RdpPreviewForm();
        form.Show();
        T Field<T>(string name) => (T)typeof(RdpPreviewForm)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        void InvokeHost(string name, params object?[] arguments) => typeof(RdpActiveXHost)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form.Host, arguments);
        var status = Field<Forms.Label>("_status");
        var resize = Field<Forms.Button>("_resize");
        if (!status.Text.StartsWith("正在连接", StringComparison.Ordinal)) {
            throw new InvalidOperationException("认证阶段必须提示完成登录，不能声称已锁定输入。");
        }
        // RDP keeps keyboard focus in a child input window while credentials are typed. Disabling the host does not
        // clear a child's focus, so a visible native child (WS_CHILD | WS_VISIBLE) stands in for it.
        var host = form.Host.Handle;
        var input = new Forms.NativeWindow();
        input.CreateHandle(new Forms.CreateParams { ClassName = "EDIT", Parent = host, Style = 0x50000000 });
        SetFocus(input.Handle);
        if (!form.Host.ContainsFocus) {
            throw new InvalidOperationException("测试前提：认证阶段 RDP 子窗口应持有焦点。");
        }
        // Exercise native HWND disabling without connecting or creating a Child Session.
        InvokeHost("OnLoginComplete");
        void AssertLocked(string stage)
        {
            Forms.Application.DoEvents();
            if (IsWindowEnabled(host) || form.Host.TabStop || form.Host.ContainsFocus) {
                throw new InvalidOperationException($"{stage} 必须在原生 RDP HWND 阻止输入，焦点不得留在 RDP 窗口。");
            }
        }
        // Relocking is only meaningful after input was really unlocked.
        void Unlock(string stage)
        {
            Field<Forms.Button>("_input").PerformClick();
            if (!IsWindowEnabled(host) || !form.Host.TabStop) {
                throw new InvalidOperationException($"{stage} 手动解锁后必须允许输入。");
            }
        }
        void AssertMode(string stage, System.Drawing.Size size, bool large)
        {
            Forms.Application.DoEvents();
            if (form.WindowState != Forms.FormWindowState.Normal || form.ClientSize != size || form.TopMost == large
                || resize.Text != (large ? "返回只读小窗" : "放大查看")) {
                throw new InvalidOperationException($"{stage} 的窗口模式、尺寸或置顶状态不符。");
            }
        }
        AssertLocked("登录后小窗");
        var smallSize = form.ClientSize;
        AssertMode("登录后小窗", smallSize, large: false);
        form.SetViewingMode(large: true);
        var largeSize = form.ClientSize;
        AssertLocked("放大后");
        Unlock("大窗");
        // Prepare completion shows the window without an explicit mode; a visible window keeps the user's choice.
        form.ShowViewing(large: null);
        if (!IsWindowEnabled(host)) {
            throw new InvalidOperationException("未指定模式的显示不得锁定用户已解锁的大窗。");
        }
        AssertMode("保持大窗", largeSize, large: true);
        form.SetViewingMode(large: false);
        AssertLocked("返回小窗");
        form.SetViewingMode(large: true);
        Unlock("隐藏前");
        form.Hide();
        AssertLocked("隐藏后");
        form.ShowViewing(large: null);
        AssertLocked("重新显示");
        AssertMode("隐藏后重新显示", smallSize, large: false);

        // Show restores stale native bounds; the requested mode must still win.
        form.WindowState = Forms.FormWindowState.Maximized;
        form.Hide();
        form.ShowViewing(large: false);
        AssertLocked("最大化后隐藏并显示小窗");
        AssertMode("最大化后隐藏并显示小窗", smallSize, large: false);
        form.WindowState = Forms.FormWindowState.Maximized;
        form.Hide();
        form.ShowViewing(large: true);
        AssertLocked("最大化后隐藏并显示大窗");
        AssertMode("最大化后隐藏并显示大窗", largeSize, large: true);
        form.WindowState = Forms.FormWindowState.Minimized;
        form.Hide();
        form.ShowViewing(large: null);
        AssertLocked("最小化后隐藏并重新显示");
        AssertMode("最小化后隐藏并重新显示", smallSize, large: false);

        form.WindowState = Forms.FormWindowState.Maximized;
        Forms.Application.DoEvents();
        if (resize.Text != "返回只读小窗") {
            throw new InvalidOperationException("标题栏最大化必须同步大窗状态和返回小窗按钮。");
        }
        resize.PerformClick();
        AssertLocked("最大化返回小窗");
        form.WindowState = Forms.FormWindowState.Maximized;
        Unlock("最大化");
        form.WindowState = Forms.FormWindowState.Normal;
        AssertLocked("标题栏还原到小窗");
        AssertMode("标题栏还原到小窗", smallSize, large: false);
        form.SetViewingMode(large: true);
        form.WindowState = Forms.FormWindowState.Maximized;
        form.WindowState = Forms.FormWindowState.Normal;
        AssertLocked("标题栏还原到大窗");
        AssertMode("标题栏还原到大窗", largeSize, large: true);
        if (form.Host.Handle != host) {
            throw new InvalidOperationException("切换观看模式不得重建 RDP HWND。");
        }

        Unlock("断开前");
        InvokeHost("ReportConnectionFailure", "RDP 连接意外断开。", 0, null);
        AssertLocked("意外断开");
        if (!status.Text.StartsWith("连接已断开", StringComparison.Ordinal) || resize.Enabled
            || Field<Forms.Button>("_input").Enabled) {
            throw new InvalidOperationException("意外断开后必须锁定输入、停用切换并说明连接已断开。");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
}
