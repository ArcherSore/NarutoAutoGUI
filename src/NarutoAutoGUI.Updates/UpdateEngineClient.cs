using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NarutoAutoGUI.Updates;

public sealed record EngineUpdate(string Version, string Notes, string Descriptor);
public sealed record EngineCheckResult(string CurrentVersion, EngineUpdate? Update);
public sealed record EngineProgress(string Phase, long Bytes, long Total, double BytesPerSecond);

public sealed class UpdateEngineClient(ProcessStartInfo start, TimeSpan? timeout = null)
{
    public const int MaxMessageBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<EngineCheckResult> CheckAsync(string installation, CancellationToken cancellation)
    {
        var result = await RunAsync(new {
            protocolVersion = 1, operation = "check", installation = Path.GetFullPath(installation)
        }, "check", null, cancellation);
        var update = result.GetProperty("update");
        return new EngineCheckResult(RequiredString(result, "currentVersion"), update.ValueKind == JsonValueKind.Null
            ? null : new EngineUpdate(RequiredString(update, "version"), RequiredString(update, "notes", true),
                RequiredString(update, "descriptor")));
    }

    public async Task<string> PrepareAsync(string installation, string descriptor,
        IProgress<EngineProgress> progress, CancellationToken cancellation)
    {
        var result = await RunAsync(new {
            protocolVersion = 1, operation = "prepare", installation = Path.GetFullPath(installation), descriptor
        }, "prepare", progress, cancellation);
        return RequiredString(result, "reference");
    }

    private async Task<JsonElement> RunAsync(object request, string operation,
        IProgress<EngineProgress>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout
            ?? TimeSpan.FromSeconds(operation == "prepare" ? 3660 : 45));
        using var immediate = operation == "prepare" ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, immediate.Token);
        var token = combined.Token;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = Utf8;
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Update Engine。");
        var stderr = DrainErrorsAsync(process.StandardError, token);
        Task? cancelWrite = null;
        CancellationTokenRegistration registration = default;
        try {
            var input = JsonSerializer.Serialize(request);
            if (Utf8.GetByteCount(input) + 1 > MaxMessageBytes) {
                throw new InvalidDataException("更新命令超出大小限制。");
            }
            await process.StandardInput.WriteLineAsync(input.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            if (operation == "prepare") {
                registration = cancellation.Register(() => {
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    cancelWrite = CancelPrepareAsync(process.StandardInput);
                });
            } else {
                process.StandardInput.Close();
            }
            JsonElement? result = null;
            using var stdout = new BufferedStream(process.StandardOutput.BaseStream);
            while (await ReadMessageAsync(stdout, token) is string line) {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.GetProperty("protocolVersion").GetInt32() != 1 || result is not null) {
                    throw new InvalidDataException("Update Engine 返回了无效消息序列。");
                }
                var type = RequiredString(message, "type");
                if (type == "progress" && operation == "prepare") {
                    var phase = RequiredString(message, "phase");
                    progress?.Report(new EngineProgress(phase,
                        phase == "download" ? message.GetProperty("bytes").GetInt64() : 0,
                        phase == "download" ? message.GetProperty("total").GetInt64() : 0,
                        phase == "download" ? message.GetProperty("bytesPerSecond").GetDouble() : 0));
                } else if (type is "result" or "error" or "cancelled") {
                    result = message.Clone();
                } else {
                    throw new InvalidDataException("Update Engine 返回了未知消息。");
                }
            }
            await process.WaitForExitAsync(token);
            await stderr;
            cancellation.ThrowIfCancellationRequested();
            if (result is not JsonElement terminal) {
                throw new InvalidDataException("Update Engine 未返回结果。");
            }
            if (RequiredString(terminal, "type") == "error" && process.ExitCode != 0) {
                throw new IOException(RequiredString(terminal, "message"));
            }
            if (process.ExitCode != 0 || RequiredString(terminal, "type") != "result"
                || RequiredString(terminal, "operation") != operation) {
                throw new InvalidDataException("Update Engine 未正常完成操作。");
            }
            return terminal;
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            throw new OperationCanceledException(cancellation);
        } catch (OperationCanceledException) {
            throw new IOException("Update Engine 操作超时，请重试。");
        } catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or DecoderFallbackException or FormatException) {
            throw new InvalidDataException("Update Engine 返回了无效消息。", exception);
        } finally {
            await registration.DisposeAsync();
            try {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            } catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception) { }
            deadline.Cancel();
            if (cancelWrite is not null) { await cancelWrite; }
            try { await stderr; } catch (Exception e) when (e is OperationCanceledException or IOException) { }
        }
    }

    private static async Task CancelPrepareAsync(StreamWriter input)
    {
        try {
            await input.WriteLineAsync("""{"protocolVersion":1,"operation":"cancel"}""");
            await input.FlushAsync();
        } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private static string RequiredString(JsonElement root, string name, bool allowEmpty = false)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException("Update Engine 返回了不完整的消息。");
        }
        var value = property.GetString()!;
        return allowEmpty || value.Length > 0 ? value
            : throw new InvalidDataException("Update Engine 返回了不完整的消息。");
    }

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, cancellation) > 0) {
            output.WriteByte(buffer[0]);
            if (output.Length > MaxMessageBytes) { throw new InvalidDataException("Update Engine 消息过大。"); }
            if (buffer[0] == '\n') { return Utf8.GetString(output.GetBuffer(), 0, (int)output.Length); }
        }
        if (output.Length > 0) { throw new InvalidDataException("Update Engine 消息不完整。"); }
        return null;
    }

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellation)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, cancellation) > 0) { }
    }
}
