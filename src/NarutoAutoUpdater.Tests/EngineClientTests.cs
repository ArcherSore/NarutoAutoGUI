using System.Diagnostics;
using System.Text.Json;
using NarutoAutoGUI.Updates;

internal static class EngineClientTests
{
    public static async Task RunAsync()
    {
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(typeof(EngineClientTests).Assembly.Location);
        start.ArgumentList.Add("--engine-fixture");
        var result = await new UpdateEngineClient(start).CheckAsync(Path.GetTempPath(), CancellationToken.None);
        if (result.CurrentVersion != "1.0.0" || result.Update?.Version != "2.0.0"
            || result.Update.Notes != "新版说明\n第二行" || result.Update.Descriptor != "opaque:do-not-parse") {
            throw new Exception("Engine result display / opaque descriptor was not preserved.");
        }
        Console.WriteLine("UPDATE TEST PASS: JSONL process adapter preserves display and opaque descriptor.");
        var linked = await new UpdateEngineClient(FixtureStart("release-url"))
            .CheckAsync(Path.GetTempPath(), CancellationToken.None);
        if (result.Update.ReleaseUrl is not null
            || linked.Update?.ReleaseUrl != "https://github.com/owner/repo/releases/tag/v2.0.0"
            || linked.Update.Descriptor != result.Update.Descriptor) {
            throw new Exception("Release URL display field changed the opaque descriptor or became required.");
        }
        Console.WriteLine("UPDATE TEST PASS: optional release URL is separate from the opaque descriptor.");
        foreach (var mode in new[] { "bad-json", "duplicate", "no-newline", "oversize", "failed-result", "error" }) {
            var invalid = FixtureStart(mode);
            try {
                await new UpdateEngineClient(invalid).CheckAsync(Path.GetTempPath(), CancellationToken.None);
                throw new Exception($"Invalid Engine response accepted: {mode}");
            } catch (Exception exception) when (exception is IOException or InvalidDataException) { }
        }
        try {
            await new UpdateEngineClient(FixtureStart("hang"), TimeSpan.FromMilliseconds(250))
                .CheckAsync(Path.GetTempPath(), CancellationToken.None);
            throw new Exception("Hanging Engine accepted.");
        } catch (IOException) { }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try {
            await new UpdateEngineClient(FixtureStart("hang")).CheckAsync(Path.GetTempPath(), cancelled.Token);
            throw new Exception("Cancelled check accepted.");
        } catch (OperationCanceledException) { }
        var noisy = await new UpdateEngineClient(FixtureStart("stderr"))
            .CheckAsync(Path.GetTempPath(), CancellationToken.None);
        if (noisy.Update?.Descriptor != result.Update.Descriptor) {
            throw new Exception("Diagnostic output corrupted the protocol.");
        }
        Console.WriteLine("UPDATE TEST PASS: invalid responses, bounded messages, timeout, cancellation and stderr.");
        var updates = new List<EngineProgress>();
        var reference = await new UpdateEngineClient(FixtureStart("prepare"))
            .PrepareAsync(Path.GetTempPath(), "opaque:do-not-parse", new CaptureProgress(updates), default);
        if (reference != "opaque:prepared" || updates.Count != 1 || updates[0].Bytes != 10) {
            throw new Exception("Prepare progress / reference was not preserved.");
        }
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try {
            await new UpdateEngineClient(FixtureStart("cancel"))
                .PrepareAsync(Path.GetTempPath(), "opaque:do-not-parse", new CaptureProgress(updates), cancel.Token);
            throw new Exception("Cancelled prepare accepted.");
        } catch (OperationCanceledException) { }
        Console.WriteLine("UPDATE TEST PASS: prepare progress, opaque reference and fixed cancellation.");
        await new UpdateEngineClient(FixtureStart("ready"))
            .InstallAsync(Path.GetTempPath(), "opaque:prepared", Environment.ProcessId, default);
        try {
            await new UpdateEngineClient(FixtureStart("not-ready"))
                .InstallAsync(Path.GetTempPath(), "opaque:prepared", Environment.ProcessId, default);
            throw new Exception("Install result accepted without ready.");
        } catch (InvalidDataException) { }
        Console.WriteLine("UPDATE TEST PASS: install returns only on ready.");
        foreach (var operation in new[] { "install", "check", "prepare" }) {
            foreach (var mode in new[] { "error-zero", "error", "copy-error" }) {
                var client = new UpdateEngineClient(FixtureStart(mode));
                try {
                    if (operation == "install") {
                        await client.InstallAsync(Path.GetTempPath(), "opaque:prepared",
                            Environment.ProcessId, default);
                    } else if (operation == "prepare") {
                        await client.PrepareAsync(Path.GetTempPath(), "opaque:do-not-parse",
                            new CaptureProgress([]), default);
                    } else {
                        await client.CheckAsync(Path.GetTempPath(), default);
                    }
                    throw new Exception($"Engine error accepted: {operation}/{mode}");
                } catch (IOException exception) {
                    if (exception.Message != "已准备更新失效，请重新下载。") {
                        throw new Exception($"Engine error message lost: {operation}/{mode}: {exception.Message}");
                    }
                }
            }
        }
        Console.WriteLine("UPDATE TEST PASS: terminal errors preserve engine messages with exit code zero or nonzero.");
        Console.WriteLine("UPDATE TEST PASS: copy pre-ready error survives a successful parent process exit.");
    }

    public static async Task RunFixtureAsync()
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var input = await Console.In.ReadLineAsync();
        using var request = JsonDocument.Parse(input!);
        if (request.RootElement.GetProperty("protocolVersion").GetInt32() != 1
            || !Path.IsPathFullyQualified(request.RootElement.GetProperty("installation").GetString()!)) {
            Environment.ExitCode = 2;
            return;
        }
        var mode = Environment.GetCommandLineArgs().Last();
        if (mode == "copy-error") {
            var start = FixtureStart("error");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            using var copy = Process.Start(start)!;
            await copy.StandardInput.WriteLineAsync(input);
            copy.StandardInput.Close();
            return; // Exit zero while the copy owns the inherited stdout and reports the error.
        }
        if (mode is "ready" or "not-ready") {
            if (request.RootElement.GetProperty("reference").GetString() != "opaque:prepared"
                || request.RootElement.GetProperty("guiPid").GetInt32() <= 0) {
                Environment.ExitCode = 2;
                return;
            }
            Console.WriteLine(JsonSerializer.Serialize(new {
                protocolVersion = 1, type = mode == "ready" ? "ready" : "result", operation = "install"
            }));
            if (mode == "ready") { await Task.Delay(200); }
            return;
        }
        if (mode is "prepare" or "cancel") {
            if (request.RootElement.GetProperty("descriptor").GetString() != "opaque:do-not-parse") {
                Environment.ExitCode = 2;
                return;
            }
            Console.WriteLine("""
                {"protocolVersion":1,"type":"progress","phase":"download","bytes":10,"total":20,"bytesPerSecond":5}
                """);
            if (mode == "cancel") {
                using var cancelMessage = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
                if (cancelMessage.RootElement.GetProperty("operation").GetString() != "cancel") {
                    Environment.ExitCode = 2;
                }
                Console.WriteLine("""{"protocolVersion":1,"type":"cancelled","operation":"prepare"}""");
            } else {
                Console.WriteLine("""
                    {"protocolVersion":1,"type":"result","operation":"prepare","reference":"opaque:prepared"}
                    """);
            }
            return;
        }
        if (mode == "hang") {
            await Task.Delay(TimeSpan.FromMinutes(1));
            return;
        }
        if (mode == "bad-json" || mode == "oversize") {
            var invalid = mode == "bad-json" ? "not json" : new string('x', UpdateEngineClient.MaxMessageBytes + 1);
            Console.WriteLine(invalid);
            return;
        }
        if (mode is "error" or "error-zero") {
            Console.WriteLine("""
                {"protocolVersion":1,"type":"error","code":"install_preflight","message":"已准备更新失效，请重新下载。"}
                """);
            Environment.ExitCode = mode == "error" ? 1 : 0;
            return;
        }
        if (mode == "stderr") {
            await Console.Error.WriteAsync(new string('x', 128 * 1024));
        }
        var result = JsonSerializer.SerializeToNode(new {
            protocolVersion = 1, type = "result", operation = "check", currentVersion = "1.0.0",
            update = new { version = "2.0.0", notes = "新版说明\n第二行", descriptor = "opaque:do-not-parse" }
        })!;
        if (mode == "release-url") {
            result["update"]!["releaseUrl"] = "https://github.com/owner/repo/releases/tag/v2.0.0";
        }
        var message = result.ToJsonString();
        if (mode == "no-newline") {
            Console.Write(message);
        } else {
            Console.WriteLine(message);
        }
        if (mode == "duplicate") {
            Console.WriteLine(message);
        }
        if (mode == "failed-result") {
            Environment.ExitCode = 1;
        }
    }

    private static ProcessStartInfo FixtureStart(string mode)
    {
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(typeof(EngineClientTests).Assembly.Location);
        start.ArgumentList.Add("--engine-fixture");
        start.ArgumentList.Add(mode);
        return start;
    }

    private sealed class CaptureProgress(List<EngineProgress> values) : IProgress<EngineProgress>
    {
        public void Report(EngineProgress value) => values.Add(value);
    }
}
