using System;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DrawingSize = System.Drawing.Size;

namespace MaaNOP.ChildSessionLauncher;

// Adapted (trimmed) from BetterGI 0.63.0 RdpActiveXHost.cs.
// Trimmed: dropped IMsRdpClientNonScriptable / SendKeys / Win+D / Win+Tab / SmartSizing toggle UI /
// reconnect state machine. Kept: MsRdpClient10 hosting, ConnectToChildSession setup, the 4 events we
// need (LoginComplete/Disconnected/FatalError/LogonError), and the diagnostic helpers.
internal sealed class RdpActiveXHost : AxHost
{
    // MsRdpClient10 (non-scriptable) CLSID.
    private const string RdpClientClsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";

    private ConnectionPointCookie? _eventCookie;
    private RdpEventSink? _eventSink;
    private bool _connectionAttemptInProgress;
    private bool _connectionFailureReported;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionDiagnostic;
    private bool _disconnectRequested;
    private bool _smartSizingEnabled = true;

    internal event EventHandler? LoginCompleted;

    internal event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    internal ChildSessionConnectionFailedEventArgs? LastConnectionDiagnostic => _lastConnectionDiagnostic;

    internal RdpActiveXHost()
        : base(RdpClientClsid)
    {
        Dock = DockStyle.Fill;
    }

    internal int ConnectedState
    {
        get
        {
            if (!IsHandleCreated)
            {
                return 0;
            }

            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "Connected"),
                CultureInfo.InvariantCulture);
        }
    }

    internal void ConnectToChildSession(DrawingSize desktopSize, string? userName = null, string? password = null)
    {
        if (ConnectedState != 0)
        {
            return;
        }

        var client = GetRequiredOcx();
        var width = Math.Clamp(desktopSize.Width, 200, 8192);
        var height = Math.Clamp(desktopSize.Height, 200, 8192);

        SetComProperty(client, "Server", "localhost");
        SetComProperty(client, "DesktopWidth", width);
        SetComProperty(client, "DesktopHeight", height);
        SetComProperty(client, "ColorDepth", 32);
        SetComProperty(client, "ConnectingText", "正在创建 MaaNOP Child Session...");
        SetComProperty(client, "DisconnectedText", "MaaNOP Child Session 已断开");

        var securedSettings = GetComProperty(client, "SecuredSettings2")
            ?? throw new COMException("RDP ActiveX 未返回 SecuredSettings2。");
        RunComStep("设置系统组合键发送位置", () =>
            SetComProperty(securedSettings, "KeyboardHookMode", 1));

        var advancedSettings = GetComProperty(client, "AdvancedSettings7")
            ?? throw new COMException("RDP ActiveX 未返回 AdvancedSettings7。");
        RunComStep("设置 RDP 连接端口", () =>
            SetComProperty(advancedSettings, "RDPPort", ChildSessionNativeMethods.GetConfiguredRdpPort()));
        RunComStep("启用 CredSSP", () =>
            SetComProperty(advancedSettings, "EnableCredSspSupport", true));
        RunComStep("启用远程 Windows 键", () =>
            SetComProperty(advancedSettings, "EnableWindowsKey", 1));
        RunComStep("设置显示缩放 (SmartSizing)", () =>
            SetComProperty(advancedSettings, "SmartSizing", _smartSizingEnabled));

        // Optional programmatic credential: when the parent session is logged on with a PIN
        // (Windows Hello), child-session auto-logon cannot reuse the password and prompts for it.
        // Supplying the account password here lets the ActiveX log on without a prompt.
        // The password is NOT stored in code; it is passed at runtime via --password.
        if (!string.IsNullOrEmpty(password))
        {
            if (!string.IsNullOrEmpty(userName))
            {
                RunComStep("设置 UserName", () => SetComProperty(client, "UserName", userName));
            }
            RunComStep("设置 ClearTextPassword", () =>
                ((IMsRdpClientNonScriptable)client).put_ClearTextPassword(password));
        }

        object connectToChildSession = true;
        RunComStep("设置 ConnectToChildSession", () =>
        {
            var extendedSettings = (IMsRdpExtendedSettings)client;
            SetAndVerifyExtendedUIntProperty(extendedSettings, "DesktopScaleFactor", 100);
            SetAndVerifyExtendedUIntProperty(extendedSettings, "DeviceScaleFactor", 100);
            TrySetExtendedProperty(extendedSettings, "EnableZoom", true);
            extendedSettings.set_Property("ConnectToChildSession", ref connectToChildSession);
        });

        _connectionAttemptInProgress = true;
        _connectionFailureReported = false;
        _lastConnectionDiagnostic = null;
        _disconnectRequested = false;
        try
        {
            RunComStep("调用 RDP Connect()", () => InvokeComMethod(client, "Connect"));
        }
        catch
        {
            _connectionAttemptInProgress = false;
            throw;
        }
    }

    internal void DisconnectSession()
    {
        _ = DisconnectCore();
    }

    private bool DisconnectCore()
    {
        if (ConnectedState == 0)
        {
            return false;
        }

        _disconnectRequested = true;
        try
        {
            InvokeComMethod(GetRequiredOcx(), "Disconnect");
            return true;
        }
        catch
        {
            _disconnectRequested = false;
            throw;
        }
    }

    protected override void CreateSink()
    {
        base.CreateSink();

        _eventSink = new RdpEventSink(this);
        _eventCookie = new ConnectionPointCookie(GetOcx(), _eventSink, typeof(IMsTscAxEvents));
    }

    protected override void DetachSink()
    {
        try
        {
            _eventCookie?.Disconnect();
            _eventCookie = null;
            _eventSink = null;
        }
        finally
        {
            base.DetachSink();
        }
    }

    private void OnLoginComplete()
    {
        _connectionAttemptInProgress = false;
        _connectionFailureReported = false;
        _lastConnectionDiagnostic = null;
        LoginCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(int disconnectReason)
    {
        var failedWhileConnecting = _connectionAttemptInProgress;
        _connectionAttemptInProgress = false;

        if (_disconnectRequested)
        {
            _disconnectRequested = false;
            return;
        }

        var extendedDisconnectReason = TryGetExtendedDisconnectReason();
        if (_connectionFailureReported)
        {
            return;
        }

        var errorDescription = TryGetErrorDescription(disconnectReason, extendedDisconnectReason);
        var failureTitle = failedWhileConnecting ? "RDP 连接失败" : "RDP 连接意外断开";
        var message = string.IsNullOrWhiteSpace(errorDescription)
            ? $"{failureTitle}。\n\n断开原因：{FormatErrorCode(disconnectReason)}\n扩展原因：{FormatErrorCode(extendedDisconnectReason)}"
            : $"{failureTitle}：{errorDescription}\n\n断开原因：{FormatErrorCode(disconnectReason)}\n扩展原因：{FormatErrorCode(extendedDisconnectReason)}";

        ReportConnectionFailure(message, disconnectReason, extendedDisconnectReason);
    }

    private void OnFatalError(int errorCode)
    {
        _connectionAttemptInProgress = false;
        if (_disconnectRequested)
        {
            return;
        }

        ReportConnectionFailure(
            $"RDP 客户端发生致命错误：{GetFatalErrorDescription(errorCode)}\n\n错误代码：{FormatErrorCode(errorCode)}",
            errorCode);
    }

    private void OnLogonError(int errorCode)
    {
        if (_disconnectRequested)
        {
            return;
        }

        var message =
            $"RDP 登录阶段：{GetLogonErrorDescription(errorCode)}\n\n错误代码：{FormatErrorCode(errorCode)}";
        if (IsNonTerminalLogonEvent(errorCode))
        {
            _lastConnectionDiagnostic =
                new ChildSessionConnectionFailedEventArgs(message, errorCode);
            return;
        }

        _connectionAttemptInProgress = false;
        ReportConnectionFailure(
            message.Replace("RDP 登录阶段：", "RDP 登录失败：", StringComparison.Ordinal),
            errorCode);
    }

    private int TryGetExtendedDisconnectReason()
    {
        try
        {
            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "ExtendedDisconnectReason"),
                CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return 0;
        }
    }

    private string? TryGetErrorDescription(int disconnectReason, int extendedDisconnectReason)
    {
        try
        {
            return Convert.ToString(
                InvokeComMethod(
                    GetRequiredOcx(),
                    "GetErrorDescription",
                    disconnectReason,
                    extendedDisconnectReason),
                CultureInfo.CurrentCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    private void ReportConnectionFailure(string message, int errorCode, int? extendedErrorCode = null)
    {
        if (_connectionFailureReported)
        {
            return;
        }

        _connectionFailureReported = true;
        ConnectionFailed?.Invoke(
            this,
            new ChildSessionConnectionFailedEventArgs(message, errorCode, extendedErrorCode));
    }

    private static string FormatErrorCode(int errorCode)
    {
        return $"{errorCode} (0x{unchecked((uint)errorCode):X8})";
    }

    private static string GetFatalErrorDescription(int errorCode) => errorCode switch
    {
        0 => "发生未知错误。",
        1 => "发生内部错误（1）。",
        2 => "内存不足。",
        3 => "无法创建 RDP 窗口。",
        4 => "发生内部错误（2）。",
        5 => "RDP 客户端进入了无效状态。",
        6 => "发生内部错误（4）。",
        7 => "建立客户端连接时发生不可恢复的错误。",
        100 => "Windows 套接字初始化失败。",
        _ => "发生未识别的致命错误。"
    };

    private static string GetLogonErrorDescription(int errorCode) => errorCode switch
    {
        -7 => "Winlogon 正在显示“拒绝断开现有会话”对话框。",
        -6 => "Winlogon 正在显示“无权限”对话框。",
        -5 => "Winlogon 正在显示会话争用选项。",
        -4 => "Winlogon 正在显示重新连接选项。",
        -3 => "Winlogon 已静默终止登录。",
        -1 => "访问被拒绝。",
        0 => "登录凭据无效。",
        1 => "密码已过期，必须先修改密码。",
        2 => "登录或登录后的处理发生错误。",
        3 => "RDP 客户端正在显示登录警告。",
        unchecked((int)0xC000006D) => "用户名或身份验证信息无效。",
        unchecked((int)0xC000006E) => "身份验证受到用户账户限制。",
        unchecked((int)0xC0000224) => "密码已过期，必须先修改密码。",
        _ => "登录阶段发生未识别的错误或事件。"
    };

    private static bool IsNonTerminalLogonEvent(int errorCode)
        => errorCode is -5 or -4 or -2 or 3;

    private object GetRequiredOcx()
    {
        if (!IsHandleCreated)
        {
            _ = Handle;
        }

        return GetOcx()
               ?? throw new InvalidOperationException(
                   "RDP ActiveX 控件尚未完成初始化，无法访问 COM 实例。");
    }

    private static object? GetComProperty(object target, string propertyName)
    {
        return target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            CultureInfo.InvariantCulture);
    }

    private static void SetComProperty(object target, string propertyName, object value)
    {
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target,
            [value],
            CultureInfo.InvariantCulture);
    }

    private static void TrySetExtendedProperty(
        IMsRdpExtendedSettings extendedSettings,
        string propertyName,
        object value)
    {
        try
        {
            extendedSettings.set_Property(propertyName, ref value);
        }
        catch (COMException)
        {
            // Older MsTscAx may not support the extended property; keep default behavior.
        }
    }

    private static void SetAndVerifyExtendedUIntProperty(
        IMsRdpExtendedSettings extendedSettings,
        string propertyName,
        uint expectedValue)
    {
        object value = expectedValue;
        extendedSettings.set_Property(propertyName, ref value);

        var actualValue = Convert.ToUInt32(
            extendedSettings.get_Property(propertyName),
            CultureInfo.InvariantCulture);
        if (actualValue != expectedValue)
        {
            throw new COMException(
                $"RDP 扩展属性 {propertyName} 写后读不一致：期望 {expectedValue}，实际 {actualValue}。");
        }
    }

    private static object? InvokeComMethod(object target, string methodName, params object[]? args)
    {
        return target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            CultureInfo.InvariantCulture);
    }

    private static void RunComStep(string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            var actualException = exception.GetBaseException();
            if (actualException is COMException comException)
            {
                throw new COMException(
                    $"{stepName}失败：{comException.Message}",
                    comException.ErrorCode);
            }

            throw;
        }
    }

    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IMsTscAxEvents
    {
        // Trimmed to the 4 events this PoC handles. Unlisted DISPIDs return
        // DISP_E_MEMBERNOTFOUND from the managed IDispatch and are ignored by the ActiveX.
        [DispId(3)]
        void OnLoginComplete();

        [DispId(4)]
        void OnDisconnected([In] int disconnectReason);

        [DispId(10)]
        void OnFatalError([In] int errorCode);

        [DispId(22)]
        void OnLogonError([In] int errorCode);
    }

    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpExtendedSettings
    {
        void set_Property(
            [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }

    // Trimmed to the single member we need (put_ClearTextPassword, vtable slot 3). Used only when
    // a password is supplied via --password, to log on without a credential prompt.
    [ComImport]
    [Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpClientNonScriptable
    {
        void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string value);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class RdpEventSink(RdpActiveXHost owner) : IMsTscAxEvents
    {
        public void OnLoginComplete() => owner.OnLoginComplete();

        public void OnDisconnected(int disconnectReason) => owner.OnDisconnected(disconnectReason);

        public void OnFatalError(int errorCode) => owner.OnFatalError(errorCode);

        public void OnLogonError(int errorCode) => owner.OnLogonError(errorCode);
    }
}

internal sealed class ChildSessionConnectionFailedEventArgs(
    string message,
    int errorCode,
    int? extendedErrorCode = null) : EventArgs
{
    public string Message { get; } = message;
    public int ErrorCode { get; } = errorCode;
    public int? ExtendedErrorCode { get; } = extendedErrorCode;
}

// Minimal host window. The child desktop stays fixed at 1920x1080; this preview window
// is smaller and uses SmartSizing so the RDP view scales to fit.
internal sealed class RdpPreviewForm : Form
{
    internal RdpActiveXHost Host { get; }

    internal RdpPreviewForm()
    {
        Text = "MaaNOP Child Session (connecting...)";
        ClientSize = new DrawingSize(1280, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Host = new RdpActiveXHost { Dock = DockStyle.Fill };
        Controls.Add(Host);
    }
}
