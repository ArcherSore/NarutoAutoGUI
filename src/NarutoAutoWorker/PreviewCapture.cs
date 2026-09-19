using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed record PreviewTarget(nint Handle, int Pid, long StartedAtUtc, uint SessionId);
internal sealed record PreviewCaptureResult(int Width, int Height, DateTime SampledAtUtc);

internal interface IPreviewCaptureSource
{
    PreviewTarget? FindTarget();
    bool IsValid(PreviewTarget target);
    IPreviewCapture Open(PreviewTarget target);
}

internal interface IPreviewCapture : IDisposable
{
    PreviewCaptureResult Capture(byte[] pixels);
}

internal sealed class MaaPreviewCaptureSource : IPreviewCaptureSource
{
    private readonly LaunchManifest _manifest;
    private readonly uint _sessionId;
    private readonly Regex _classRegex;
    private readonly Regex _windowRegex;

    internal MaaPreviewCaptureSource(LaunchManifest manifest, uint sessionId)
    {
        _manifest = manifest;
        _sessionId = sessionId;
        _classRegex = new Regex(manifest.Controller.ClassRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        _windowRegex = new Regex(manifest.Controller.WindowRegex, RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    public PreviewTarget? FindTarget()
    {
        using var windows = MaaToolkit.Shared.Desktop.Window.Find();
        foreach (var window in windows) {
            if (!_classRegex.IsMatch(window.ClassName) || !_windowRegex.IsMatch(window.Name)) {
                continue;
            }
            GetWindowThreadProcessId(window.Handle, out var pid);
            if (pid == 0) {
                continue;
            }
            try {
                using var process = Process.GetProcessById(checked((int)pid));
                if ((uint)process.SessionId == _sessionId) {
                    return new PreviewTarget(window.Handle, process.Id, process.StartTime.ToUniversalTime().Ticks,
                        _sessionId);
                }
            } catch (ArgumentException) {
                // A window can disappear during discovery.
            }
        }
        return null;
    }

    public bool IsValid(PreviewTarget target)
    {
        GetWindowThreadProcessId(target.Handle, out var pid);
        if (pid != target.Pid) {
            return false;
        }
        try {
            using var process = Process.GetProcessById(target.Pid);
            return !process.HasExited && (uint)process.SessionId == target.SessionId
                && process.StartTime.ToUniversalTime().Ticks == target.StartedAtUtc;
        } catch (ArgumentException) {
            return false;
        }
    }

    public IPreviewCapture Open(PreviewTarget target) => new MaaPreviewCapture(target, _manifest);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

internal sealed class MaaPreviewCapture : IPreviewCapture
{
    private readonly MaaWin32Controller _controller;
    private readonly MaaImageBuffer _image;
    private readonly byte[] _raw = new byte[PreviewBuffer.MaximumPixelBytes];

    internal MaaPreviewCapture(PreviewTarget target, LaunchManifest manifest)
    {
        _controller = new MaaWin32Controller(target.Handle,
            Enum.Parse<Win32ScreencapMethods>(manifest.Controller.ScreencapMethod),
            (Win32InputMethod)0, (Win32InputMethod)0, LinkOption.Start, CheckStatusOption.ThrowIfNotSucceeded);
        try {
            _image = new MaaImageBuffer();
            if (!_controller.SetOption(ControllerOption.ScreenshotTargetLongSide,
                ProtocolConstants.MaximumPreviewPixelWidth)) {
                throw new InvalidOperationException("Preview 缩放选项设置失败。");
            }
        } catch {
            _image?.Dispose();
            _controller.Dispose();
            throw;
        }
    }

    public PreviewCaptureResult Capture(byte[] pixels)
    {
        if (_controller.Screencap().Wait() != MaaJobStatus.Succeeded || !_controller.GetCachedImage(_image)
            || _image.IsEmpty) {
            throw new IOException("Maa Preview 截图失败。");
        }
        var info = _image.GetInfo();
        if (info.Width <= 0 || info.Height <= 0) {
            throw new InvalidDataException("Maa Preview 尺寸非法。");
        }
        var scale = Math.Min(1D, Math.Min((double)ProtocolConstants.MaximumPreviewPixelWidth / info.Width,
            (double)ProtocolConstants.MaximumPreviewPixelHeight / info.Height));
        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));
        if ((width != info.Width || height != info.Height) && !_image.TryResize(width, height)) {
            throw new IOException("Maa Preview 缩放失败。");
        }
        if (!_image.TryGetRawData(out var data) || _image.Channels is not (3 or 4)) {
            throw new InvalidDataException("Maa Preview 像素格式非法。");
        }
        var channels = _image.Channels;
        var count = width * height;
        Marshal.Copy(data, _raw, 0, count * channels);
        for (var index = 0; index < count; index++) {
            var input = index * channels;
            var output = index * 4;
            pixels[output] = _raw[input];
            pixels[output + 1] = _raw[input + 1];
            pixels[output + 2] = _raw[input + 2];
            pixels[output + 3] = 255;
        }
        return new PreviewCaptureResult(width, height, DateTime.UtcNow);
    }

    public void Dispose()
    {
        _image.Dispose();
        _controller.Dispose();
    }
}
