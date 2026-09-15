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
    }

    public static async Task RunFixtureAsync()
    {
        var input = await Console.In.ReadLineAsync();
        using var request = JsonDocument.Parse(input!);
        if (request.RootElement.GetProperty("operation").GetString() != "check"
            || request.RootElement.GetProperty("protocolVersion").GetInt32() != 1
            || !Path.IsPathFullyQualified(request.RootElement.GetProperty("installation").GetString()!)) {
            Environment.ExitCode = 2;
            return;
        }
        var mode = Environment.GetCommandLineArgs().Last();
        if (mode == "hang") {
            await Task.Delay(TimeSpan.FromMinutes(1));
            return;
        }
        if (mode == "bad-json" || mode == "oversize") {
            var invalid = mode == "bad-json" ? "not json" : new string('x', UpdateEngineClient.MaxMessageBytes + 1);
            Console.WriteLine(invalid);
            return;
        }
        if (mode == "error") {
            Console.WriteLine("""{"protocolVersion":1,"type":"error","code":"network_error","message":"失败"}""");
            Environment.ExitCode = 1;
            return;
        }
        if (mode == "stderr") {
            await Console.Error.WriteAsync(new string('x', 128 * 1024));
        }
        var message = JsonSerializer.Serialize(new {
            protocolVersion = 1, type = "result", operation = "check", currentVersion = "1.0.0",
            update = new { version = "2.0.0", notes = "新版说明\n第二行", descriptor = "opaque:do-not-parse" }
        });
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
}
