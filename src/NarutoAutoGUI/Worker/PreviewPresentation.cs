using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

// One replaceable frame and one queued UI callback; a slow dispatcher never retains a frame backlog.
internal sealed class PreviewPresentation
{
    private readonly object _gate = new();
    private readonly byte[] _pixels = new byte[PreviewBuffer.MaximumPixelBytes];
    private readonly byte[] _displayPixels = new byte[PreviewBuffer.MaximumPixelBytes];
    private PreviewBuffer? _buffer;
    private PreviewFrameInfo? _frame;

    internal void Sample(PreviewBuffer? buffer, PreviewResponse response)
    {
        lock (_gate) {
            _buffer = buffer;
            if (_frame is null || response.Generation > _frame.Generation) {
                var state = response.State == PreviewState.WaitingForWindow
                    ? PreviewState.WaitingForWindow : PreviewState.WaitingForFrame;
                _frame = new PreviewFrameInfo(state, response.Generation,
                    0, DateTime.UnixEpoch, 0, 0);
            }
            if (buffer is not null && buffer.TryRead(_pixels, out var frame) && frame is not null
                && frame.Generation >= _frame.Generation) {
                _frame = frame;
            }
        }
    }

    internal void Present(Action<PreviewFrameInfo?, byte[]> display)
    {
        PreviewFrameInfo? frame;
        lock (_gate) {
            frame = _frame;
            // Recheck the target immediately before committing pixels, including while the UI was queued.
            if (_buffer is not null) {
                if (!_buffer.TryReadInfo(out var current) || current is null) {
                    return;
                }
                if (_frame is not null && current.Generation < _frame.Generation) {
                    frame = _frame;
                } else if (_frame is null || current.Generation != _frame.Generation || current.Revision == 0) {
                    frame = current with { State = current.State == PreviewState.WaitingForWindow
                        ? PreviewState.WaitingForWindow : PreviewState.WaitingForFrame,
                        Revision = 0, Width = 0, Height = 0 };
                }
            }
            if (frame is { Revision: > 0 }) {
                _pixels.AsSpan(0, frame.Width * frame.Height * 4).CopyTo(_displayPixels);
            }
        }
        // Only one callback may execute at once. The renderer never holds the reader/renewal gate.
        display(frame, _displayPixels);
    }

    internal void Reset()
    {
        lock (_gate) {
            _buffer = null;
            _frame = null;
        }
    }
}
