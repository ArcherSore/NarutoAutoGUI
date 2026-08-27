using System.Runtime.InteropServices;
using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.ChildSession;

internal sealed class ChildSessionManager : IDisposable
{
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private RdpPreviewForm? _previewForm;
    private ChildSessionService? _service;
    private bool _disposed;

    internal ChildSessionManager(AppLogger logger)
    {
        _logger = logger;
    }

    internal event EventHandler<ChildSessionSnapshot>? StateChanged;

    internal ChildSessionSnapshot Snapshot { get; private set; } = ChildSessionSnapshot.Empty;

    internal bool HasChildSession => ChildSessionService.TryGetChildSessionId() is not null;

    internal uint? DetectExistingSession()
    {
        try {
            var sessionId = ChildSessionService.TryGetChildSessionId();
            if (sessionId is null) {
                UpdateState(ChildSessionState.NotRunning, null, 0, "未检测到桌面分身");
                _logger.Info("启动检测：当前没有 Child Session。");
                return null;
            }

            UpdateState(ChildSessionState.Existing, sessionId, 0, "检测到已有桌面分身，正在恢复连接");
            _logger.Info($"启动检测：发现已有 Child Session {sessionId}。");
            return sessionId;
        } catch (Exception exception) {
            _logger.Error("检测已有 Child Session 失败。", exception);
            UpdateState(ChildSessionState.Faulted, null, 0, exception.GetBaseException().Message);
            return null;
        }
    }

    internal async Task<uint> EnsureConnectedAsync(bool showPreview, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try {
            ThrowIfDisposed();
            uint? currentSessionId = null;
            try {
                currentSessionId = ChildSessionService.TryGetChildSessionId();
                var connectedState = SafeConnectedState();
                if (_service?.ChildSessionId is uint connectedId && currentSessionId == connectedId
                    && Snapshot.State is (ChildSessionState.ConnectedVisible
                        or ChildSessionState.ConnectedHidden)
                    && connectedState == 1) {
                    SetPreviewVisibility(showPreview);
                    _logger.Debug($"复用已连接的 Child Session {connectedId}，show={showPreview}。");
                    return connectedId;
                }

                DisposePreview(disconnect: true);
                UpdateState(
                    ChildSessionState.Connecting, currentSessionId, 0,
                    currentSessionId is null ? "正在创建桌面分身" : "正在恢复桌面分身连接");

                _logger.Info("检测并启用 Child Session 支持（需要管理员权限）。");
                _logger.Debug($"ChildSessionsEnabled(before)={ChildSessionService.IsChildSessionsEnabled()}，"
                              + $"RdpPort={ChildSessionService.GetRdpPort()}，"
                              + $"RdpWrapper={ChildSessionService.IsRdpWrapPresent()}（仅信息）。");
                ChildSessionService.EnsureChildSessionsEnabled();

                var preview = new RdpPreviewForm();
                var service = new ChildSessionService(preview);
                preview.Host.ConnectionFailed += OnConnectionFailed;
                preview.FormClosing += OnPreviewFormClosing;
                _previewForm = preview;
                _service = service;

                // AxHost creation and interactive credential prompts require a visible native window.
                preview.Show();
                preview.Activate();
                _logger.Info("正在连接 RDP Child Session（固定 1920×1080 @ 100%，SmartSizing 仅用于预览）。");

                await service.ConnectAsync(cancellationToken);

                var sessionId = service.ChildSessionId
                    ?? throw new InvalidOperationException("RDP 登录完成后未取得 childSessionId。");
                preview.Text = $"NarutoAutoGUI Child Session #{sessionId} (1920x1080 @ 100%)";
                _logger.Info($"Child Session 已连接：childSessionId={sessionId}，ConnectedState={service.ConnectedState}。");
                SetPreviewVisibility(showPreview);
                return sessionId;
            } catch (Exception exception) {
                var actualException = exception.GetBaseException();
                if (actualException is COMException comException && unchecked((uint)comException.ErrorCode) == 0x80040111) {
                    var descriptiveException = new InvalidOperationException(
                        "初始化 RDP ActiveX 控件失败 (0x80040111 CLASS_E_CLASSNOTAVAILABLE)。"
                        + "系统无法提供请求的远程桌面类工厂，请确认系统远程桌面组件完整且显卡驱动正常。",
                        exception);
                    _logger.Error("RDP Child Session 创建或恢复失败。", descriptiveException);
                    UpdateState(
                        ChildSessionState.Faulted, SafeChildSessionId(currentSessionId),
                        SafeConnectedState(), descriptiveException.Message);
                    throw descriptiveException;
                }

                _logger.Error("RDP Child Session 创建或恢复失败。", exception);
                UpdateState(
                    ChildSessionState.Faulted, SafeChildSessionId(currentSessionId),
                    SafeConnectedState(), exception.GetBaseException().Message);
                throw;
            }
        } finally {
            _operationLock.Release();
        }
    }

    internal void ShowPreview()
    {
        if (_previewForm is null || SafeConnectedState() != 1) {
            throw new InvalidOperationException("桌面分身尚未建立 RDP 连接，请先创建或恢复连接。");
        }

        SetPreviewVisibility(show: true);
    }

    internal void HidePreview()
    {
        if (_previewForm is null || SafeConnectedState() != 1) {
            return;
        }

        SetPreviewVisibility(show: false);
    }

    internal async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        uint? sessionId = null;
        try {
            ThrowIfDisposed();
            sessionId = ChildSessionService.TryGetChildSessionId();
            UpdateState(ChildSessionState.Disconnecting, sessionId, SafeConnectedState(), "正在结束桌面分身");
            _logger.Info(sessionId is null
                ? "结束请求：当前没有 Child Session。"
                : $"正在断开 RDP 并注销 Child Session {sessionId}。");

            DisposePreview(disconnect: true);
            var terminatedId = ChildSessionService.TerminateChildSession(wait: true);
            _logger.Info(terminatedId is null
                ? "当前无 Child Session 需要注销。"
                : $"已注销 Child Session {terminatedId.Value}。");
            UpdateState(ChildSessionState.NotRunning, null, 0, "未运行");
        } catch (Exception exception) {
            _logger.Error("结束 Child Session 失败。", exception);
            UpdateState(
                ChildSessionState.Faulted, SafeChildSessionId(sessionId),
                SafeConnectedState(), exception.GetBaseException().Message);
            throw;
        } finally {
            _operationLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) {
            return;
        }

        _disposed = true;
        DisposePreview(disconnect: true);
        _operationLock.Dispose();
    }

    private void SetPreviewVisibility(bool show)
    {
        if (_previewForm is null || _service is null) {
            return;
        }

        var connectedState = SafeConnectedState();
        if (connectedState != 1) {
            throw new InvalidOperationException(
                $"RDP 当前未处于已连接状态（ConnectedState={connectedState}），需要重新建立连接。");
        }

        if (show) {
            _previewForm.Show();
            _previewForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
            _previewForm.Activate();
        } else {
            _previewForm.Hide();
        }

        var sessionId = _service.ChildSessionId ?? SafeChildSessionId(Snapshot.ChildSessionId);
        UpdateState(
            show ? ChildSessionState.ConnectedVisible : ChildSessionState.ConnectedHidden,
            sessionId, connectedState,
            show ? "已连接，子桌面可见" : "已连接，子桌面已隐藏");
        _logger.Info(show ? "已显示子桌面。" : "已隐藏子桌面（RDP 连接保持存活）。");
    }

    private void OnPreviewFormClosing(object? sender, System.Windows.Forms.FormClosingEventArgs e)
    {
        if (_disposed || Snapshot.State == ChildSessionState.Disconnecting) {
            return;
        }

        e.Cancel = true;
        _previewForm?.Hide();
        var connectedState = SafeConnectedState();
        if (connectedState != 1) {
            _logger.Info($"关闭子桌面窗口已转换为隐藏；RDP 当前状态为 {connectedState}，保留现有故障/连接中状态。");
            return;
        }

        UpdateState(
            ChildSessionState.ConnectedHidden,
            _service?.ChildSessionId ?? SafeChildSessionId(Snapshot.ChildSessionId), connectedState,
            "已连接，子桌面已隐藏");
        _logger.Info("关闭子桌面窗口已转换为隐藏；RDP 连接保持存活。");
    }

    private void OnConnectionFailed(object? sender, ChildSessionConnectionFailedEventArgs e)
    {
        _logger.Error(
            $"RDP 状态异常：{e.Message}；ErrorCode={e.ErrorCode}"
            + (e.ExtendedErrorCode is int extended ? $"；ExtendedErrorCode={extended}" : string.Empty));
        UpdateState(
            ChildSessionState.Faulted, SafeChildSessionId(Snapshot.ChildSessionId),
            SafeConnectedState(), e.Message.Replace(Environment.NewLine, " "));
    }

    private void DisposePreview(bool disconnect)
    {
        var preview = _previewForm;
        var service = _service;
        _previewForm = null;
        _service = null;

        if (preview is null) {
            return;
        }

        preview.Host.ConnectionFailed -= OnConnectionFailed;
        preview.FormClosing -= OnPreviewFormClosing;
        if (disconnect) {
            try {
                service?.Disconnect();
                _logger.Debug("已请求断开 RDP ActiveX 连接。");
            } catch (Exception exception) {
                _logger.Warn("断开 RDP ActiveX 时发生可忽略异常。", exception);
            }
        }

        preview.Close();
        preview.Dispose();
    }

    private int SafeConnectedState()
    {
        try {
            return _service?.ConnectedState ?? 0;
        } catch (Exception exception) {
            _logger.Debug($"读取 RDP ConnectedState 失败：{exception.GetBaseException().Message}");
            return 0;
        }
    }

    private uint? SafeChildSessionId(uint? fallback)
    {
        try {
            return ChildSessionService.TryGetChildSessionId();
        } catch (Exception exception) {
            _logger.Warn("读取 Child Session ID 失败，状态展示保留最近一次已知值。", exception);
            return fallback;
        }
    }

    private void UpdateState(ChildSessionState state, uint? sessionId, int connectedState, string detail)
    {
        Snapshot = new ChildSessionSnapshot(state, sessionId, connectedState, detail);
        StateChanged?.Invoke(this, Snapshot);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
