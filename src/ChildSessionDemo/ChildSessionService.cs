using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DrawingSize = System.Drawing.Size;

namespace MaaNOP.ChildSessionLauncher;

// Adapted (heavily trimmed) from BetterGI 0.63.0 ChildSessionService.cs.
// Dropped: DI/logging/DispatcherTimer/retry-reconnect/InstanceService/Config/WPF window.
// Kept: the connect->await LoginCompleted(timeout 60s)->report failure flow, and the
// enable / query / logoff surface (delegated to ChildSessionNativeMethods).
// All ActiveX access happens on the UI thread (caller is the RdpPreviewForm, single STA thread).
internal sealed class ChildSessionService
{
    private static readonly DrawingSize DefaultDesktopSize = new(1920, 1080);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    private readonly RdpPreviewForm _form;
    private readonly RdpActiveXHost _host;
    private TaskCompletionSource<bool>? _connectionTcs;
    private ChildSessionConnectionFailedEventArgs? _lastFailure;

    public uint? ChildSessionId { get; private set; }

    public int ConnectedState { get; private set; }

    public ChildSessionConnectionFailedEventArgs? LastFailure => _lastFailure;

    public ChildSessionService(RdpPreviewForm form)
    {
        _form = form;
        _host = form.Host;
    }

    public static bool IsChildSessionsEnabled() =>
        ChildSessionNativeMethods.IsChildSessionsEnabled();

    public static void EnsureChildSessionsEnabled()
    {
        if (!ChildSessionNativeMethods.IsChildSessionsEnabled())
        {
            ChildSessionNativeMethods.EnableChildSessions();
        }
    }

    public static uint? TryGetChildSessionId() =>
        ChildSessionNativeMethods.TryGetChildSessionId();

    public static int GetRdpPort() =>
        ChildSessionNativeMethods.GetConfiguredRdpPort();

    public static bool IsRdpWrapPresent() =>
        ChildSessionNativeMethods.IsRdpWrapperEnabled();

    public static uint? TerminateChildSession(bool wait) =>
        ChildSessionNativeMethods.TerminateChildSession(wait);

    public async Task ConnectAsync(CancellationToken cancellationToken, string? userName = null, string? password = null)
    {
        _lastFailure = null;
        _connectionTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _host.LoginCompleted += OnLoginCompleted;
        _host.ConnectionFailed += OnConnectionFailed;

        try
        {
            _host.ConnectToChildSession(DefaultDesktopSize, userName, password);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectionTimeout);

            bool ok;
            try
            {
                ok = await _connectionTcs.Task
                    .WaitAsync(timeoutCts.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                TryDisconnect();
                throw new InvalidOperationException(
                    $"桌面分身连接及登录初始化未能在 {ConnectionTimeout.TotalSeconds:0} 秒内完成。");
            }

            if (!ok)
            {
                TryDisconnect();
                throw new InvalidOperationException(
                    _lastFailure?.Message ?? "RDP 连接失败。");
            }

            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            ConnectedState = _host.ConnectedState;
        }
        finally
        {
            _host.LoginCompleted -= OnLoginCompleted;
            _host.ConnectionFailed -= OnConnectionFailed;
            _connectionTcs = null;
        }
    }

    public void Disconnect() => TryDisconnect();

    private void TryDisconnect()
    {
        try
        {
            _host.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            Console.WriteLine($"RDP disconnect 忽略异常：{exception.GetBaseException().Message}");
        }
    }

    private void OnLoginCompleted(object? sender, EventArgs e)
    {
        _lastFailure = null;
        _connectionTcs?.TrySetResult(true);
    }

    private void OnConnectionFailed(object? sender, ChildSessionConnectionFailedEventArgs e)
    {
        _lastFailure = e;
        _connectionTcs?.TrySetResult(false);
    }
}
