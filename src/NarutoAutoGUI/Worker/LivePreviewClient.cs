using System.Diagnostics;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal sealed class LivePreviewClient(WorkerCoordinator coordinator, Action<Exception> logFailure)
{
    private long _lastFailureLog;

    internal async Task RunAsync(Guid workerId, Func<Action, Task> dispatch,
        Action<PreviewFrameInfo?, byte[]> display, CancellationToken cancellationToken, Func<bool>? isPaused = null)
    {
        var presentation = new PreviewPresentation();
        Task? pendingDisplay = null;
        while (!cancellationToken.IsCancellationRequested) {
            var request = new PreviewRequest(workerId, Guid.NewGuid(), isPaused?.Invoke() == true);
            PreviewBuffer? buffer = null;
            try {
                var response = await coordinator.SendPreviewAsync(ProtocolOperations.PreviewStart, request,
                    cancellationToken);
                var lastRenew = Stopwatch.GetTimestamp();
                while (!cancellationToken.IsCancellationRequested) {
                    var started = Stopwatch.GetTimestamp();
                    var paused = isPaused?.Invoke() == true;
                    // Discover the new buffer promptly after startup or restoring a hidden preview.
                    var renewInterval = buffer is null ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2);
                    if (paused != request.Paused || Stopwatch.GetElapsedTime(lastRenew) >= renewInterval) {
                        request = request with { Paused = paused };
                        response = await coordinator.SendPreviewAsync(ProtocolOperations.PreviewRenew, request,
                            cancellationToken);
                        lastRenew = Stopwatch.GetTimestamp();
                    }
                    if (buffer is null && response.Descriptor is { } descriptor) {
                        buffer = PreviewBuffer.OpenRead(descriptor);
                    }
                    if (!paused) {
                        presentation.Sample(buffer, response);
                    }
                    if (pendingDisplay is { IsCompleted: true }) {
                        var completed = pendingDisplay;
                        pendingDisplay = null;
                        await completed;
                    }
                    if (!paused) {
                        pendingDisplay ??= dispatch(() => presentation.Present(display));
                    }
                    var remaining = TimeSpan.FromMilliseconds(ProtocolConstants.PreviewIntervalMilliseconds)
                        - Stopwatch.GetElapsedTime(started);
                    if (remaining > TimeSpan.Zero) {
                        await Task.Delay(remaining, cancellationToken);
                    }
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return;
            } catch (Exception exception) {
                if (_lastFailureLog == 0 || Stopwatch.GetElapsedTime(_lastFailureLog) >= TimeSpan.FromSeconds(30)) {
                    _lastFailureLog = Stopwatch.GetTimestamp();
                    logFailure(exception);
                }
                presentation.Reset();
                pendingDisplay ??= dispatch(() => presentation.Present(display));
            } finally {
                presentation.Reset();
                buffer?.Dispose();
                try {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await coordinator.SendPreviewAsync(ProtocolOperations.PreviewStop, request, timeout.Token);
                } catch {
                    // Connection loss or lease expiry also revokes the producer.
                }
            }
            try {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }
}
