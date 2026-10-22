using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace NarutoAutoGUI.ChildSession;

// The existing RDP host stays in this window for its entire connection lifetime.
internal sealed partial class RdpPreviewWindow
{
    // Title bar, top and bottom frame and bottom inset; side frames and insets. The picture keeps 16:9 inside.
    private const double ChromeHeight = 37;
    private const double ChromeWidth = 6;

    private bool _large;
    private bool _interactive;
    private bool _connected;
    private bool _disconnected;
    private bool _changingVisibility;
    private bool? _modeBeforeMaximize;

    internal RdpActiveXHost Host { get; } = new();

    // True while mouse and keyboard reach the Child Session; the main window shows it on its desktop toggle.
    internal bool Interactive => _interactive;

    internal event EventHandler? InteractiveChanged;

    internal RdpPreviewWindow()
    {
        InitializeComponent();
        RdpFormsHost.Child = Host;
        Host.LoginCompleted += (_, _) =>
        {
            _connected = true;
            _disconnected = false;
            SetViewingMode(large: false);
        };
        // Raised for failures and unexpected disconnects; a requested disconnect closes this window instead.
        Host.ConnectionFailed += (_, _) =>
        {
            _connected = false;
            _disconnected = true;
            LockInput();
        };
        StateChanged += (_, _) =>
        {
            if (!_connected || _changingVisibility || !IsVisible) {
                return;
            }
            if (WindowState == WindowState.Maximized) {
                _modeBeforeMaximize ??= _large;
                _large = true;
                Topmost = false;
                RefreshToolbar();
            } else if (WindowState == WindowState.Normal && _modeBeforeMaximize is bool large) {
                SetViewingMode(large);
            }
        };
        RefreshToolbar();
    }

    // Shows the window and brings it forward. A visible window keeps its mode; a hidden window reopens small.
    internal void ShowViewing()
    {
        if (!IsVisible) {
            _large = false;
            ChangeVisibility(Show);
            if (_connected) {
                // Show restores native maximized bounds; apply the small size after that restoration.
                SetViewingMode(large: false);
            }
        } else if (WindowState == WindowState.Minimized) {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    // Locks while still visible, so focus can leave the RDP window before it hides.
    internal void HideViewing()
    {
        LockInput();
        ChangeVisibility(Hide);
    }

    internal void SetViewingMode(bool large)
    {
        LockInput();
        _large = large;
        _modeBeforeMaximize = null;
        Topmost = !large;
        WindowState = WindowState.Normal;
        var area = WorkArea();
        var width = Math.Min(large ? 1280 : 560, area.Width - 32);
        Width = width;
        Height = Math.Min((width - ChromeWidth) * 9 / 16 + ChromeHeight, area.Height - 32);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        RefreshToolbar();
    }

    protected override void OnClosed(EventArgs e)
    {
        RdpFormsHost.Child = null;
        Host.Dispose();
        base.OnClosed(e);
    }

    private void ChangeVisibility(Action change)
    {
        _changingVisibility = true;
        try {
            change();
        } finally {
            _changingVisibility = false;
        }
    }

    // The monitor's working area in this window's DIPs.
    private Rect WorkArea()
    {
        var area = Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    private void LockInput() => SetInteractive(false);

    private void SetInteractive(bool interactive)
    {
        var wasInteractive = _interactive;
        _interactive = interactive && _large && IsVisible;
        RefreshToolbar();
        if (!_interactive) {
            // The RDP keyboard hook follows focus rather than the enabled state, so focus leaves before disabling.
            ViewerRoot.Focus();
        }
        Host.SetInputBlocked(!_interactive);
        if (_interactive != wasInteractive) {
            InteractiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshToolbar()
    {
        SizeButton.IsEnabled = _connected;
        PinButton.IsEnabled = _connected;
        InputButton.IsEnabled = _connected && _large;
        SizeIcon.Symbol = _large ? WpfSymbolRegular.ArrowMinimize16 : WpfSymbolRegular.ArrowMaximize16;
        SizeText.Text = _large ? "小窗" : "放大";
        SizeButton.ToolTip = _large ? "返回置顶只读小窗" : "放大查看，放大后可允许操作";
        PinIcon.Symbol = Topmost ? WpfSymbolRegular.Pin16 : WpfSymbolRegular.PinOff16;
        PinButton.ToolTip = Topmost ? "取消置顶" : "置顶";
        InputButton.Tag = _interactive ? "On" : null;
        InputIcon.Symbol = _interactive ? WpfSymbolRegular.LockOpen16 : WpfSymbolRegular.LockClosed16;
        InputText.Text = _interactive ? "可操作" : "只读";
        InputButton.ToolTip = !_connected ? "连接后可观看分身"
            : !_large ? "只读观看 · 放大后可允许操作"
            : _interactive ? "可操作 · 输入可能影响正在执行的任务，点击锁定为只读"
            : "只读观看 · 鼠标、滚轮和键盘不传入分身，点击允许操作";
        PictureFrame.BorderBrush = (WpfBrush)FindResource(_interactive ? "Brush.Warning" : "Brush.Canvas");
        StatusDot.Fill = (WpfBrush)FindResource(_disconnected ? "Brush.Error"
            : _connected ? "Brush.Success" : "Brush.Primary");
        StatusText.Text = _disconnected ? "连接已断开 · 输入已锁定，请回到主窗口处理"
            : !_connected ? "正在连接 · 如有登录提示，请先完成登录" : string.Empty;
    }

    private void InputButton_Click(object sender, RoutedEventArgs e) => SetInteractive(!_interactive);

    private void SizeButton_Click(object sender, RoutedEventArgs e) => SetViewingMode(!_large);

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        RefreshToolbar();
    }

    // SmartSizing stretches the fixed 1920×1080 desktop to the host, so the host keeps 16:9 inside the frame.
    private void PictureArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var frame = PictureFrame.BorderThickness;
        var width = Math.Max(0, Math.Min(e.NewSize.Width - frame.Left - frame.Right,
            (e.NewSize.Height - frame.Top - frame.Bottom) * 16 / 9));
        RdpFormsHost.Width = width;
        RdpFormsHost.Height = width * 9 / 16;
    }
}
