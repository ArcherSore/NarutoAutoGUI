using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed record PreviewImageData(DateTime SampledAtUtc, int PixelWidth, int PixelHeight, byte[] PngBytes);

internal sealed record LatestPreviewFrame(
    Guid RunId,
    long Revision,
    DateTime SampledAtUtc,
    int PixelWidth,
    int PixelHeight,
    byte[] PngBytes);

internal interface IPreviewFrameSource
{
    PreviewImageData? ReadLatest();
}

internal sealed class MaaCachedImageFrameSource(MaaWin32Controller controller) : IPreviewFrameSource
{
    public PreviewImageData? ReadLatest()
    {
        using var image = new MaaImageBuffer();
        if (!controller.GetCachedImage(image) || image.IsEmpty)
        {
            return null;
        }

        var info = image.GetInfo();
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new InvalidDataException("MaaFramework cached image 尺寸非法。 ");
        }

        var scale = Math.Min(
            1D,
            Math.Min(
                (double)ProtocolConstants.MaximumPreviewPixelWidth / info.Width,
                (double)ProtocolConstants.MaximumPreviewPixelHeight / info.Height));
        var targetWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(info.Height * scale));
        if ((targetWidth != info.Width || targetHeight != info.Height)
            && !image.TryResize(targetWidth, targetHeight))
        {
            throw new InvalidOperationException("缩放 MaaFramework cached image 失败。 ");
        }

        if (!image.TryGetEncodedData(out byte[]? pngBytes) || pngBytes is null || pngBytes.Length == 0)
        {
            throw new InvalidOperationException("编码 MaaFramework cached image PNG 失败。 ");
        }
        if (pngBytes.Length > ProtocolConstants.MaximumPreviewPngBytes)
        {
            throw new InvalidDataException(
                $"Preview PNG 超过预算：{pngBytes.Length} > {ProtocolConstants.MaximumPreviewPngBytes} bytes。 ");
        }

        return new PreviewImageData(DateTime.UtcNow, targetWidth, targetHeight, pngBytes);
    }
}

internal sealed class LatestFramePreview
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(
        ProtocolConstants.PreviewIntervalMilliseconds);
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Guid _runId;
    private readonly IPreviewFrameSource _source;
    private readonly Action<string, string, string> _log;
    private DateTime _nextSampleAtUtc = DateTime.MinValue;
    private DateTime _nextFailureLogAtUtc = DateTime.MinValue;
    private LatestPreviewFrame? _latest;
    private long _revision;
    private bool _stopped;

    internal LatestFramePreview(Guid runId, IPreviewFrameSource source, Action<string, string, string> log)
    {
        _runId = runId;
        _source = source;
        _log = log;
    }

    internal void Pump(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
        }
        if (nowUtc < _nextSampleAtUtc)
        {
            return;
        }
        _nextSampleAtUtc = nowUtc + SampleInterval;

        try
        {
            var image = _source.ReadLatest();
            if (image is null)
            {
                return;
            }
            if (image.PixelWidth <= 0
                || image.PixelHeight <= 0
                || image.PngBytes.Length == 0
                || image.PngBytes.Length > ProtocolConstants.MaximumPreviewPngBytes
                || image.SampledAtUtc.Kind != DateTimeKind.Utc)
            {
                throw new InvalidDataException("Preview frame 数据非法或超过预算。 ");
            }

            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }
                if (_latest is not null && _latest.PngBytes.AsSpan().SequenceEqual(image.PngBytes))
                {
                    return;
                }
                _revision++;
                _latest = new LatestPreviewFrame(
                    _runId,
                    _revision,
                    image.SampledAtUtc,
                    image.PixelWidth,
                    image.PixelHeight,
                    image.PngBytes);
            }
        }
        catch (Exception exception)
        {
            LogFailure(nowUtc, exception);
        }
    }

    internal LatestPreviewFrame? ReadLatest()
    {
        lock (_gate)
        {
            return _latest;
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _latest = null;
        }
    }

    private void LogFailure(DateTime nowUtc, Exception exception)
    {
        if (nowUtc < _nextFailureLogAtUtc)
        {
            return;
        }
        _nextFailureLogAtUtc = nowUtc + FailureLogInterval;
        try
        {
            _log("WARN", "preview.capture", $"Preview 采样失败：{exception.GetBaseException().Message}");
        }
        catch
        {
            // Preview diagnostics must never escape into Run execution.
        }
    }
}
