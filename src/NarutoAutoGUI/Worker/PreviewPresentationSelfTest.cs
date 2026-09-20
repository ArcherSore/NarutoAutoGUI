using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static class PreviewPresentationSelfTest
{
    internal static void Run()
    {
        var identity = new PreviewIdentity(Guid.NewGuid(), 1, Guid.NewGuid());
        using var writer = PreviewBuffer.Create(identity);
        using var reader = PreviewBuffer.OpenRead(writer.Descriptor);
        var presentation = new PreviewPresentation();
        var pixels = new byte[PreviewBuffer.MaximumPixelBytes];
        var response = new PreviewResponse(identity, PreviewState.Streaming, 1, writer.Descriptor);
        writer.TryPublish(1, 1, DateTime.UtcNow, 4, 3, pixels);
        presentation.Sample(reader, response);
        // The queued callback has not run yet. Invalidation must win without another background sample.
        writer.TryClear(2, PreviewState.WaitingForWindow);
        presentation.Present((frame, _) =>
        {
            if (frame is not { Generation: 2, Revision: 0, State: PreviewState.WaitingForWindow }) {
                throw new InvalidOperationException("UI 提交前未拒绝失效目标。");
            }
        });
        // A newer control response must not be replaced by the previous target's mapped frame.
        presentation.Sample(reader, response with { Generation = 3, State = PreviewState.WaitingForFrame });
        presentation.Present((frame, _) =>
        {
            if (frame is not { Generation: 3, Revision: 0, State: PreviewState.WaitingForFrame }) {
                throw new InvalidOperationException("旧映射覆盖了新目标的等待状态。");
            }
        });
        // A slow UI consumes the latest replacement, never its originally queued pixels.
        for (var revision = 1; revision <= 100; revision++) {
            Array.Fill(pixels, (byte)revision);
            writer.TryPublish(3, revision, DateTime.UtcNow, 4, 3, pixels);
            presentation.Sample(reader, response);
        }
        presentation.Present((frame, received) =>
        {
            if (frame is not { Generation: 3, Revision: 100 } || received[0] != 100) {
                throw new InvalidOperationException("慢 UI 未合并为最新完整帧。");
            }
        });
        writer.TryClear(3, PreviewState.WaitingForWindow);
        presentation.Present((frame, _) =>
        {
            if (frame is not { Generation: 3, Revision: 0, State: PreviewState.WaitingForWindow }) {
                throw new InvalidOperationException("同代次清空后仍显示旧帧。");
            }
        });
        presentation.Reset();
        presentation.Present((frame, _) =>
        {
            if (frame is not null) {
                throw new InvalidOperationException("取消后排队的 UI 仍提交旧帧。");
            }
        });
    }
}
