using System.Text;
using System.Text.Json;
using NarutoAutoGUI.Protocol;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace NarutoAutoWorker;

// Run telemetry through the Worker's IPC surface, observed as the envelopes a Sentry transport would send.
internal static class RunTelemetrySelfTests
{
    internal static readonly TelemetryOptions Options = new("https://public@o1.ingest.sentry.io/1",
        "NarutoAutoGUI@1.0.0+test@1", "production", true, 1, 1) { Enabled = true };

    internal static (string Name, Action Body)[] Checks => [
        Check(VerifyRunTransactions), Check(VerifyTelemetryToggle), Check(VerifyTransactionSampling),
        Check(VerifyTelemetryFaults), Check(VerifyRedactionInFinalEnvelope)
    ];

    private static (string Name, Action Body) Check(Func<Task> body) =>
        ($"RunTelemetry.{body.Method.Name}", () => body().GetAwaiter().GetResult());

    // One Transaction per Run and one Span per Plan Item, pretask included; no Error Event before redaction exists.
    private static async Task VerifyRunTransactions()
    {
        var transport = new CapturingTransport();
        var (succeeded, failed, stopped) = (Guid.Empty, Guid.Empty, Guid.Empty);
        await using (var harness = await TelemetryHarnessAsync(Options, transport)) {
            var options = ProtocolJson.ToElement(new Dictionary<string, object> {
                ["Mode"] = "Fast", ["Targets"] = new[] { "A", "B" },
                ["Login"] = new Dictionary<string, string> { ["password"] = "••••••", ["account"] = "", ["note"] = "x" }
            });
            var plan = PlanWithPretask(options);
            var request = RunSelfTests.Request(plan) with { RedactionValues = ["redact-me-77"] };
            succeeded = request.RunId;
            RunSelfTests.Expect(await harness.StartAsync(request) == "accepted", "带脱敏值的计划应被接受。");
            var pretask = await harness.Executions.ExecutingAsync(plan.Items[0].PlanItemId);
            pretask.ReportRunning();
            pretask.Complete(new RuntimeExecutionResult(RuntimeExecutionOutcome.Succeeded,
                ProtocolJson.ToElement(new { exitCode = 0 }), null));
            var task = await harness.Executions.ExecutingAsync(plan.Items[1].PlanItemId);
            task.ReportRunning();
            task.Complete(RuntimeExecutionOutcome.Succeeded);
            await harness.WaitForStateAsync(s => s.LastRun?.RunId == succeeded, "第一个 Run 应终结。");

            var snapshot = await harness.RequestAsync(ProtocolOperations.WorkerGetSnapshot, new { });
            var logs = await harness.RequestAsync(ProtocolOperations.LogGetSince, new LogGetSinceRequest(0, 500));
            RunSelfTests.Expect(!snapshot.Data.GetRawText().Contains("redact-me-77", StringComparison.Ordinal)
                && !logs.Data.GetRawText().Contains("redact-me-77", StringComparison.Ordinal),
                "RedactionValues 不得进入 Snapshot 或 Worker 日志。");

            var failing = RunSelfTests.Request(RunSelfTests.Plan(1));
            failed = failing.RunId;
            await harness.StartAsync(failing);
            (await harness.Executions.ExecutingAsync(failing.Plan.Items[0].PlanItemId))
                .Complete(RuntimeExecutionOutcome.Failed, "MaaTaskFailed");
            await harness.WaitForStateAsync(s => s.LastRun?.RunId == failed, "失败的 Run 应终结。");

            var stopping = RunSelfTests.Request(RunSelfTests.Plan(2));
            stopped = stopping.RunId;
            await harness.StartAsync(stopping);
            var running = await harness.Executions.ExecutingAsync(stopping.Plan.Items[0].PlanItemId);
            running.ReportRunning();
            await harness.StopAsync(stopped);
            await running.StopCalledAsync();
            running.Complete(RuntimeExecutionOutcome.Cancelled);
            await harness.WaitForStateAsync(s => s.LastRun?.RunId == stopped, "停止的 Run 应终结。");
        }

        var transactions = transport.Items("transaction").ToArray();
        RunSelfTests.Expect(transactions.Length == 3 && transport.Items("event").Count() == 0,
            $"三个 Run 应各有一个 Transaction 且没有 Error Event，实际 {transactions.Length}。");
        var byRun = transactions.ToDictionary(item => item.GetProperty("tags").GetProperty("run.id").GetString()!);
        var first = byRun[succeeded.ToString()];
        var spans = first.GetProperty("spans").EnumerateArray().ToArray();
        RunSelfTests.Expect(Status(first) == "ok" && spans.Length == 2
            && spans[0].GetProperty("op").GetString() == "pretask" && spans[1].GetProperty("op").GetString() == "task"
            && spans.All(span => Status(span) == "ok"), "成功 Run 的 Transaction 与每项 Span 应为 ok，pretask 单独标注。");
        var pretaskData = spans[0].GetProperty("data");
        var taskData = spans[1].GetProperty("data");
        RunSelfTests.Expect(pretaskData.GetProperty("exit_code").GetInt32() == 0
            && pretaskData.TryGetProperty("duration_ms", out _)
            && taskData.GetProperty("option.Mode").GetString() == "Fast"
            && taskData.GetProperty("option.Targets").GetString() == "A,B"
            && taskData.GetProperty("option.Login.password").GetString() == "filled"
            && taskData.GetProperty("option.Login.account").GetString() == "empty"
            && taskData.GetProperty("option.Login.note").GetString() == "filled",
            "Span 应带退出码、耗时和选项摘要；输入只报 filled/empty。");
        var second = byRun[failed.ToString()];
        RunSelfTests.Expect(Status(second) == "internal_error"
            && second.GetProperty("spans").EnumerateArray().Single() is var failedSpan
            && Status(failedSpan) == "internal_error", "失败的 Run 与 Plan Item 应为 internal_error。");
        var third = byRun[stopped.ToString()];
        RunSelfTests.Expect(Status(third) == "cancelled"
            && third.GetProperty("spans").EnumerateArray().Single() is var stoppedSpan
            && Status(stoppedSpan) == "cancelled",
            "停止的 Run 应为 cancelled，未开始的项不产生 Span。");

        var text = transport.Text;
        RunSelfTests.Expect(!text.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)
            && !text.Contains("\"server_name\":\"" + Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !text.Contains("redact-me-77", StringComparison.Ordinal),
            "上报内容不得包含机器名、Windows 用户名或 RedactionValues。");
        var user = first.GetProperty("user").GetProperty("id").GetString()!;
        RunSelfTests.Expect(user.Length == 64 && user.All(Uri.IsHexDigit) && user == user.ToLowerInvariant()
            && user == TelemetryIdentity.AnonymousUserId(), "user.id 应为 MachineGuid 的加盐 SHA-256。");
    }

    // Turned off, nothing new is collected and the current Run is dropped; turned on, the next Run is reported.
    private static async Task VerifyTelemetryToggle()
    {
        var transport = new CapturingTransport();
        Guid reported;
        await using (var harness = await TelemetryHarnessAsync(Options with { Enabled = false }, transport)) {
            async Task<(RunStartRequest Request, ScriptedExecution Execution)> StartAsync()
            {
                var request = RunSelfTests.Request(RunSelfTests.Plan(1));
                await harness.StartAsync(request);
                var execution = await harness.Executions.ExecutingAsync(request.Plan.Items[0].PlanItemId);
                execution.ReportRunning();
                return (request, execution);
            }
            async Task FinishAsync((RunStartRequest Request, ScriptedExecution Execution) run)
            {
                run.Execution.Complete(RuntimeExecutionOutcome.Succeeded);
                await harness.WaitForStateAsync(s => s.LastRun?.RunId == run.Request.RunId, "Run 应终结。");
            }
            async Task SetAsync(bool enabled)
            {
                var response = await harness.RequestAsync(ProtocolOperations.FrameworkSetOptions,
                    new FrameworkSetOptionsRequest(new(), enabled));
                RunSelfTests.Expect(response.Success == true, "framework.setOptions 应成功。");
            }

            var startedWhileOff = await StartAsync();
            await SetAsync(true);
            await FinishAsync(startedWhileOff);
            var next = await StartAsync();
            reported = next.Request.RunId;
            await FinishAsync(next);
            var droppedWhileRunning = await StartAsync();
            await SetAsync(false);
            await FinishAsync(droppedWhileRunning);
            await FinishAsync(await StartAsync());
        }
        var transactions = transport.Items("transaction").ToArray();
        RunSelfTests.Expect(transactions.Length == 1
            && transactions[0].GetProperty("tags").GetProperty("run.id").GetString() == reported.ToString(),
            "只有开关开启期间开始并结束的 Run 应上报。");
    }

    // tracing=false reports errors only, and a zero sample rate drops every Transaction.
    private static async Task VerifyTransactionSampling()
    {
        foreach (var options in new[] { Options with { Tracing = false }, Options with { TracesSampleRate = 0 } }) {
            var transport = new CapturingTransport();
            await using (var harness = await TelemetryHarnessAsync(options, transport)) {
                await CompleteRunAsync(harness);
            }
            RunSelfTests.Expect(!transport.Items("transaction").Any(), $"不应发送 Transaction：{options}");
        }
    }

    // A broken transport, SDK or observer changes nothing the GUI can see about Runs, and never faults the Worker.
    private static async Task VerifyTelemetryFaults()
    {
        foreach (var transport in new[] { new CapturingTransport { Fail = true }, new() { Hang = true } }) {
            await using var harness = await TelemetryHarnessAsync(Options, transport);
            await CompleteRunAsync(harness);
            await CompleteRunAsync(harness);
        }
        await using (var harness = await TelemetryHarnessAsync(Options with { Dsn = "not a dsn" },
            new CapturingTransport())) {
            await CompleteRunAsync(harness);
        }
        await using (var harness = await RunHarness.CreateAsync(telemetry: Options,
            createTelemetry: _ => new ThrowingTelemetry())) {
            await CompleteRunAsync(harness);
            RunSelfTests.Expect((await harness.RequestAsync(ProtocolOperations.FrameworkSetOptions,
                new FrameworkSetOptionsRequest(new(), true))).Success == true, "遥测异常不得让 setOptions 失败。");
        }
    }

    // Whatever finally leaves the process — Transaction, Error Event, Sentry Log — carries no password the Run used
    // (raw or JSON-escaped) and no profile folder name; redacted items are still delivered, not silently dropped.
    // The names here are invented: redaction follows PI password inputs, never a particular project.
    private static async Task VerifyRedactionInFinalEnvelope()
    {
        const string pin = "731904";
        const string phrase = "pa\"ss\\wörd密钥";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var transport = new CapturingTransport();
        await using (var harness = await TelemetryHarnessAsync(Options, transport)) {
            var options = ProtocolJson.ToElement(new Dictionary<string, object> {
                ["Mode"] = $"use-{pin}", ["Other"] = "Normal",
                ["Credentials"] = new Dictionary<string, string> { ["secret"] = "••••••" }
            });
            var plan = new RunPlan(ProtocolConstants.PlanVersion, DateTime.UtcNow, RunHarness.Project,
                RunHarness.ProfileDigest, ProtocolJson.ToElement(new { }), [
                    new(Guid.NewGuid(), $"Sync-{pin}", "同步", "SyncEntry", options, ProtocolJson.ToElement(new { }))
                ]);
            var request = RunSelfTests.Request(plan) with { RedactionValues = [pin, phrase, ""] };
            await harness.StartAsync(request);
            var execution = await harness.Executions.ExecutingAsync(plan.Items[0].PlanItemId);
            execution.ReportRunning();
            // Stand-ins for the uploads later tickets add: an Agent stderr line in an exception, a log line, an extra.
            Sentry.SentrySdk.CaptureException(new InvalidOperationException(
                $"agent.stderr: login {pin} with {phrase} from {profile}\\agent"));
            Sentry.SentrySdk.CaptureMessage($"worker log: {phrase}", scope => scope.SetExtra("detail",
                new { text = $"{pin} at {profile}", nested = new[] { phrase } }));
            // A log's text is fixed once created: callers scrub first, and one that still leaks is refused.
            Sentry.SentrySdk.Logger.LogWarning($"maafw: input_text {pin} {phrase} {profile}");
            // An item whose scrubbing fails is dropped, never sent as it was.
            Sentry.SentrySdk.CaptureMessage("unscrubbable item", scope => scope.SetExtra("value", new Unreadable()));
            execution.Complete(RuntimeExecutionOutcome.Succeeded);
            await harness.WaitForStateAsync(s => s.LastRun?.RunId == request.RunId, "Run 应终结。");
        }

        var text = transport.Text;
        foreach (var secret in new[] { pin, phrase, profile }) {
            foreach (var form in new[] { secret, JsonEncodedText.Encode(secret).ToString(),
                JsonEncodedText.Encode(secret, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
                    .ToString() }) {
                RunSelfTests.Expect(!text.Contains(form, StringComparison.OrdinalIgnoreCase),
                    $"最终 envelope 不得包含密码或用户目录：{form}");
            }
        }
        var transaction = transport.Items("transaction").Single();
        var span = transaction.GetProperty("spans").EnumerateArray().Single();
        RunSelfTests.Expect(span.GetProperty("data").GetProperty("option.Other").GetString() == "Normal"
            && span.GetProperty("data").GetProperty("option.Mode").GetString()!.Contains("[Filtered]"),
            "Span 中的密码应被替换，其余内容保留；空值不得导致整体替换。");
        RunSelfTests.Expect(!transport.Text.Contains("unscrubbable item"), "清洗失败的上报必须丢弃。");
        RunSelfTests.Expect(transport.Items("event").Count() == 2 && transport.Text.Contains("[Filtered]"),
            "含密码的事件应脱敏后照常发送，而不是被丢弃。");
    }

    private static async Task CompleteRunAsync(RunHarness harness)
    {
        var request = RunSelfTests.Request(RunSelfTests.Plan(2));
        RunSelfTests.Expect(await harness.StartAsync(request) == "accepted", "遥测故障不得影响接受 Run。");
        foreach (var item in request.Plan.Items) {
            var execution = await harness.Executions.ExecutingAsync(item.PlanItemId);
            execution.ReportRunning();
            execution.Complete(RuntimeExecutionOutcome.Succeeded);
        }
        var final = await harness.WaitForStateAsync(s => s.LastRun?.RunId == request.RunId, "Run 应终结。");
        RunSelfTests.Expect(final is { WorkerState: WorkerState.Ready, LastRun.State: RunState.Succeeded }
            && final.LastRun.Items.All(item => item.State == PlanItemState.Succeeded),
            "遥测不得改变 Run 结果、Plan Item 状态或 Worker State。");
    }

    private static Task<RunHarness> TelemetryHarnessAsync(TelemetryOptions options, CapturingTransport transport) =>
        RunHarness.CreateAsync(telemetry: options,
            createTelemetry: manifest => RunTelemetry.Create(manifest, (_, _, _) => { }, transport));

    private static RunPlan PlanWithPretask(JsonElement taskOptions)
    {
        var empty = ProtocolJson.ToElement(new { });
        RunPlanItem[] items = [
            new(Guid.NewGuid(), "Launch", "启动", string.Empty, empty, ProtocolJson.ToElement(Array.Empty<object>())) {
                Kind = PlanItemKind.Pretask, Program = new PretaskCommand("launch.exe", ["--fast"])
            },
            new(Guid.NewGuid(), "Daily", "日常", "DailyEntry", taskOptions, empty)
        ];
        return new RunPlan(ProtocolConstants.PlanVersion, DateTime.UtcNow, RunHarness.Project,
            RunHarness.ProfileDigest, empty, items);
    }

    private static string? Status(JsonElement item) =>
        item.TryGetProperty("status", out var status) ? status.GetString()
        : item.GetProperty("contexts").GetProperty("trace").GetProperty("status").GetString();

    private sealed class Unreadable
    {
        public string Value => throw new InvalidOperationException("scripted unreadable value");
    }

    private sealed class ThrowingTelemetry : IRunTelemetry
    {
        public void SetEnabled(bool enabled) => throw new InvalidOperationException("scripted telemetry fault");
        public void Register(IReadOnlyList<string> redactionValues) =>
            throw new InvalidOperationException("scripted telemetry fault");
        public void Observe(WorkerSnapshot snapshot) => throw new InvalidOperationException("scripted telemetry fault");
        public void Dispose() => throw new InvalidOperationException("scripted telemetry fault");
    }
}

// Keeps every envelope the SDK sends, serialized as it would go over the wire.
internal sealed class CapturingTransport : ITransport
{
    private readonly object _gate = new();
    private readonly List<byte[]> _envelopes = [];

    internal bool Fail { get; init; }

    // Never completes until the SDK gives up, like a network that accepts the connection and then stalls.
    internal bool Hang { get; init; }

    public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (Fail) {
            throw new IOException("scripted transport failure");
        }
        if (Hang) {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        using var stream = new MemoryStream();
        await envelope.SerializeAsync(stream, null, cancellationToken);
        lock (_gate) {
            _envelopes.Add(stream.ToArray());
        }
    }

    internal string Text
    {
        get
        {
            lock (_gate) {
                return string.Join("\n", _envelopes.Select(Encoding.UTF8.GetString));
            }
        }
    }

    // The JSON payloads of every item of the given type, in the order sent.
    internal IEnumerable<JsonElement> Items(string type)
    {
        byte[][] envelopes;
        lock (_gate) {
            envelopes = _envelopes.ToArray();
        }
        foreach (var envelope in envelopes) {
            foreach (var (itemType, payload) in Parse(envelope)) {
                if (itemType == type) {
                    yield return JsonDocument.Parse(payload).RootElement.Clone();
                }
            }
        }
    }

    internal static IEnumerable<(string Type, byte[] Payload)> Parse(byte[] envelope)
    {
        var position = Line(envelope, 0).Next;
        while (position < envelope.Length) {
            var (header, next) = Line(envelope, position);
            if (header.Length == 0) {
                position = next;
                continue;
            }
            using var document = JsonDocument.Parse(header);
            var type = document.RootElement.GetProperty("type").GetString()!;
            byte[] payload;
            if (document.RootElement.TryGetProperty("length", out var length)) {
                payload = envelope.AsSpan(next, length.GetInt32()).ToArray();
                next = Math.Min(envelope.Length, next + length.GetInt32() + 1);
            } else {
                (payload, next) = Line(envelope, next);
            }
            yield return (type, payload);
            position = next;
        }
    }

    private static (byte[] Content, int Next) Line(byte[] data, int start)
    {
        var end = Array.IndexOf(data, (byte)'\n', start);
        return end < 0 ? (data[start..], data.Length) : (data[start..end], end + 1);
    }
}
