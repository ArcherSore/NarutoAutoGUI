using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NarutoAutoGUI.Updates;

public sealed record EngineUpdate(string Version, string Notes, string Descriptor);
public sealed record EngineCheckResult(string CurrentVersion, EngineUpdate? Update);

public sealed class UpdateEngineClient(ProcessStartInfo start, TimeSpan? timeout = null)
{
    public const int MaxMessageBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<EngineCheckResult> CheckAsync(string installation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = Utf8;
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Update Engine。");
        var stderr = DrainErrorsAsync(process.StandardError, token);
        try {
            var request = JsonSerializer.Serialize(new {
                protocolVersion = 1, operation = "check", installation = Path.GetFullPath(installation)
            });
            if (Utf8.GetByteCount(request) + 1 > MaxMessageBytes) {
                throw new InvalidDataException("更新命令超出大小限制。");
            }
            await process.StandardInput.WriteLineAsync(request.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            process.StandardInput.Close();
            var output = await ReadMessageAsync(process.StandardOutput.BaseStream, token);
            await process.WaitForExitAsync(token);
            await stderr;
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.GetProperty("protocolVersion").GetInt32() != 1) {
                throw new InvalidDataException("Update Engine 协议版本不兼容。");
            }
            var type = root.GetProperty("type").GetString();
            if (type == "error" && process.ExitCode != 0) {
                throw new IOException(RequiredString(root, "message"));
            }
            if (process.ExitCode != 0 || type != "result" || root.GetProperty("operation").GetString() != "check") {
                throw new InvalidDataException("Update Engine 未正常完成检查。");
            }
            var currentVersion = RequiredString(root, "currentVersion");
            var update = root.GetProperty("update");
            return new EngineCheckResult(currentVersion, update.ValueKind == JsonValueKind.Null ? null
                : new EngineUpdate(RequiredString(update, "version"), RequiredString(update, "notes", allowEmpty: true),
                    RequiredString(update, "descriptor")));
        } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
            throw new IOException("Update Engine 检查超时，请重试。");
        } catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or DecoderFallbackException or FormatException) {
            throw new InvalidDataException("Update Engine 返回了无效消息。", exception);
        } finally {
            try {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                }
            } catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception) { }
            deadline.Cancel();
            try {
                await stderr;
            } catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        }
    }

    private static string RequiredString(JsonElement root, string name, bool allowEmpty = false)
    {
        var value = root.GetProperty(name).GetString();
        return value is not null && (allowEmpty || value.Length > 0) ? value
            : throw new InvalidDataException("Update Engine 返回了不完整的消息。");
    }

    private static async Task<string> ReadMessageAsync(Stream stream, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0) {
            if (output.Length + count > MaxMessageBytes) {
                throw new InvalidDataException("Update Engine 消息超出大小限制。");
            }
            output.Write(buffer, 0, count);
        }
        var text = Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        if (!text.EndsWith('\n') || text.TrimEnd('\r', '\n').Contains('\n')) {
            throw new InvalidDataException("Update Engine 必须返回单条完整 JSONL 消息。");
        }
        return text;
    }

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellation)
    {
        // Drain diagnostic output without retaining an unbounded buffer or confusing it with protocol messages.
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, cancellation) > 0) { }
    }
}
