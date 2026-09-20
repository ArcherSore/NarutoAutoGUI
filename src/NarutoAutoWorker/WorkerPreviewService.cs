using System.Diagnostics;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed class WorkerPreviewService : IDisposable
{
    private sealed record Interest(Guid ConnectionId, PreviewIdentity Identity);
    private readonly object _gate = new();
    private readonly Guid _workerId;
    private readonly uint _sessionId;
    private readonly IPreviewCaptureSource _source;
    private readonly Action<string, string, string> _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private Interest? _desired;
    private long _renewedAt;
    private PreviewResponse? _response;
    private long _nextFailureLogAt;

    internal WorkerPreviewService(Guid workerId, uint sessionId, IPreviewCaptureSource source,
        Action<string, string, string> log)
    {
        _workerId = workerId;
        _sessionId = sessionId;
        _source = source;
        _log = log;
        _loop = Task.Run(RunAsync);
    }

    internal PreviewResponse Handle(string operation, Guid connectionId, PreviewRequest request)
    {
        if (request.WorkerInstanceId != _workerId || request.SubscriptionId == Guid.Empty) {
            throw new WorkerRequestException("invalid_request", "Preview identity 非法。");
        }
        var identity = new PreviewIdentity(_workerId, _sessionId, request.SubscriptionId);
        lock (_gate) {
            var matches = _desired is { } current
                && current.ConnectionId == connectionId && current.Identity == identity;
            if (operation == ProtocolOperations.PreviewStop) {
                if (matches) {
                    _desired = null;
                }
                return new PreviewResponse(identity, PreviewState.Retiring, 0, null);
            }
            if (operation == ProtocolOperations.PreviewStart) {
                if (!matches) {
                    _desired = new Interest(connectionId, identity);
                    _response = new PreviewResponse(identity, PreviewState.Preparing, 0, null);
                }
            } else if (!matches || LeaseExpired()) {
                throw new WorkerRequestException("preview_expired", "Preview 订阅已失效。");
            }
            _renewedAt = Stopwatch.GetTimestamp();
            return _response!;
        }
    }

    internal void Disconnect(Guid connectionId)
    {
        lock (_gate) {
            if (_desired?.ConnectionId == connectionId) {
                _desired = null;
            }
        }
    }

    private bool LeaseExpired() => Stopwatch.GetElapsedTime(_renewedAt) >= TimeSpan.FromSeconds(6);

    private bool IsCurrent(Interest interest)
    {
        lock (_gate) {
            return !_shutdown.IsCancellationRequested && ReferenceEquals(_desired, interest) && !LeaseExpired();
        }
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested) {
            Interest? interest;
            lock (_gate) {
                interest = LeaseExpired() ? null : _desired;
            }
            if (interest is not null) {
                try {
                    await RunSubscriptionAsync(interest);
                } catch (Exception exception) {
                    LogFailure(exception);
                    SetState(interest, PreviewState.Unavailable, 0, null);
                    // A failed transport is replaced only by a fresh subscription, never by a file overwrite.
                    lock (_gate) {
                        if (ReferenceEquals(_desired, interest)) {
                            _desired = null;
                        }
                    }
                }
            }
            try {
                await Task.Delay(200, _shutdown.Token);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    private async Task RunSubscriptionAsync(Interest interest)
    {
        using var buffer = PreviewBuffer.Create(interest.Identity);
        var pixels = new byte[PreviewBuffer.MaximumPixelBytes];
        IPreviewCapture? capture = null;
        Task<PreviewCaptureResult>? pending = null;
        PreviewTarget? target = null;
        long generation = 0, revision = 0;
        long nextCheck = 0, nextCapture = 0;
        PreviewState? pendingClear = PreviewState.WaitingForWindow;
        SetState(interest, PreviewState.WaitingForWindow, generation, buffer.Descriptor);
        try {
            while (IsCurrent(interest)) {
                var now = Stopwatch.GetTimestamp();
                if (now >= nextCheck) {
                    nextCheck = now + 2 * Stopwatch.Frequency;
                    if (target is not null && !_source.IsValid(target)) {
                        target = null;
                        generation++;
                        revision = 0;
                        SetState(interest, PreviewState.WaitingForWindow, generation, buffer.Descriptor);
                        pendingClear = PreviewState.WaitingForWindow;
                    }
                    if (target is null && pending is null) {
                        DisposeCapture(ref capture);
                        target = _source.FindTarget();
                        if (target is not null) {
                            generation++;
                            SetState(interest, PreviewState.WaitingForFrame, generation, buffer.Descriptor);
                            pendingClear = PreviewState.WaitingForFrame;
                        }
                    }
                }
                if (pendingClear is { } clear && buffer.TryClear(generation, clear)) {
                    pendingClear = null;
                }
                if (pending is { IsCompleted: true }) {
                    PreviewCaptureResult? frame = null;
                    try {
                        frame = await pending;
                    } catch (Exception exception) {
                        LogFailure(exception);
                        nextCapture = now + 2 * Stopwatch.Frequency;
                    }
                    pending = null;
                    if (frame is not null) {
                        lock (_gate) {
                            if (target is not null && IsCurrent(interest)
                                && buffer.TryPublish(generation, revision + 1, frame.SampledAtUtc,
                                    frame.Width, frame.Height, pixels)) {
                                revision++;
                                pendingClear = null;
                                SetState(interest, PreviewState.Streaming, generation, buffer.Descriptor);
                            }
                        }
                    }
                    if (target is null) {
                        DisposeCapture(ref capture);
                    }
                }
                if (target is not null && pending is null && now >= nextCapture) {
                    var captureTarget = target;
                    nextCapture = now + (long)Math.Ceiling(Stopwatch.Frequency
                        * ProtocolConstants.PreviewIntervalMilliseconds / 1000D);
                    pending = Task.Run(() =>
                    {
                        capture ??= _source.Open(captureTarget);
                        return capture.Capture(pixels);
                    });
                }
                await Task.Delay(8, _shutdown.Token);
            }
        } finally {
            // This wait belongs only to the preview owner loop, never to Run stop or the IPC reader.
            try {
                if (pending is not null) {
                    await pending;
                }
            } catch (Exception exception) {
                LogFailure(exception);
            }
            DisposeCapture(ref capture);
        }
    }

    private static void DisposeCapture(ref IPreviewCapture? capture)
    {
        try {
            capture?.Dispose();
        } finally {
            capture = null;
        }
    }

    private void SetState(Interest interest, PreviewState state, long generation, PreviewDescriptor? descriptor)
    {
        lock (_gate) {
            if (ReferenceEquals(_desired, interest)) {
                _response = new PreviewResponse(interest.Identity, state, generation, descriptor);
            }
        }
    }

    private void LogFailure(Exception exception)
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextFailureLogAt) {
            return;
        }
        _nextFailureLogAt = now + 30 * Stopwatch.Frequency;
        try {
            _log("WARN", "preview.capture", exception.GetBaseException().Message);
        } catch {
            // Diagnostics cannot alter Run state.
        }
    }

    public void Dispose()
    {
        lock (_gate) {
            _desired = null;
            _shutdown.Cancel();
        }
        // Native calls may not be cancellable; let the owner dispose after the call returns.
        _ = _loop.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
    }
}
