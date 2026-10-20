using System.Drawing;
using System.Windows.Forms;

namespace NarutoAutoGUI.ChildSession;

// The existing RDP host stays in this form for its entire connection lifetime.
internal sealed class RdpPreviewForm : Form
{
    private readonly Button _resize = new() { AutoSize = true, MinimumSize = new Size(110, 44) };
    private readonly Button _pin = new() { AutoSize = true, MinimumSize = new Size(100, 44) };
    private readonly Button _input = new() { AutoSize = true, MinimumSize = new Size(110, 44) };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 36 };
    private bool _large;
    private bool _interactive;
    private bool _connected;
    private bool _disconnected;
    private bool _changingVisibility;
    private bool? _modeBeforeMaximize;

    internal RdpActiveXHost Host { get; } = new() { Dock = DockStyle.Fill };

    internal RdpPreviewForm()
    {
        Text = "桌面分身 · 正在连接";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(500, 360);
        ClientSize = new Size(1280, 800);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(6) };
        toolbar.Controls.AddRange([_resize, _pin, _input]);
        Controls.Add(Host);
        Controls.Add(_status);
        Controls.Add(toolbar);
        _resize.Click += (_, _) => SetViewingMode(!_large);
        _pin.Click += (_, _) => { TopMost = !TopMost; RefreshToolbar(); };
        _input.Click += (_, _) => SetInteractive(!_interactive);
        Host.LoginCompleted += (_, _) =>
        {
            _connected = true;
            _disconnected = false;
            SetViewingMode(large: false);
        };
        // Raised for failures and unexpected disconnects; a requested disconnect disposes this form instead.
        Host.ConnectionFailed += (_, _) =>
        {
            _connected = false;
            _disconnected = true;
            LockInput();
        };
        Resize += (_, _) =>
        {
            if (!_connected || _changingVisibility || !Visible) {
                return;
            }
            if (WindowState == FormWindowState.Maximized) {
                _modeBeforeMaximize ??= _large;
                _large = true;
                TopMost = false;
                RefreshToolbar();
            } else if (WindowState == FormWindowState.Normal && _modeBeforeMaximize is bool large) {
                SetViewingMode(large);
            }
        };
        RefreshToolbar();
    }

    // Shows the window and brings it forward. large selects a read-only mode explicitly; null keeps the mode of a
    // visible window, while a hidden window reopens small.
    internal void ShowViewing(bool? large)
    {
        if (!Visible) {
            _large = large ?? false;
            Show();
        } else if (large is bool requested) {
            SetViewingMode(requested);
        } else if (WindowState == FormWindowState.Minimized) {
            WindowState = FormWindowState.Normal;
        }
        Activate();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!value) {
            // Lock while still visible, so focus can leave the RDP window before it hides.
            LockInput();
        }
        _changingVisibility = true;
        try {
            base.SetVisibleCore(value);
        } finally {
            _changingVisibility = false;
        }
        if (value && _connected) {
            // Show restores native maximized bounds; apply the requested size after that restoration.
            SetViewingMode(_large);
        }
    }

    internal void SetViewingMode(bool large)
    {
        LockInput();
        _large = large;
        _modeBeforeMaximize = null;
        TopMost = !large;
        WindowState = FormWindowState.Normal;
        var area = Screen.FromControl(this).WorkingArea;
        var width = Math.Min(large ? 1280 : 560, area.Width - 32);
        var height = Math.Min(width * 9 / 16 + 92, area.Height - 32);
        ClientSize = new Size(width, height);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        RefreshToolbar();
    }

    private void LockInput() => SetInteractive(false);

    private void SetInteractive(bool interactive)
    {
        _interactive = interactive && _large && Visible;
        // The toolbar is refreshed first: right after login the button only becomes focusable here.
        RefreshToolbar();
        if (!_interactive) {
            // The RDP keyboard hook follows focus rather than the enabled state, so focus leaves before disabling.
            _resize.Focus();
        }
        Host.SetInputBlocked(!_interactive);
    }

    private void RefreshToolbar()
    {
        _resize.Enabled = _connected;
        _pin.Enabled = _connected;
        _input.Enabled = _connected;
        _resize.Text = _large ? "返回只读小窗" : "放大查看";
        _pin.Text = TopMost ? "置顶：开" : "置顶：关";
        _input.Visible = _large;
        _input.Text = _interactive ? "锁定为只读" : "允许操作";
        _status.Text = _disconnected ? "连接已断开 · 输入已锁定，请回到主窗口处理"
            : !_connected ? "正在连接 · 如有登录提示，请先完成登录"
            : _interactive ? "允许操作 · 输入可能影响正在执行的任务"
            : "只读观看 · 鼠标、滚轮和键盘不传入分身";
    }
}
