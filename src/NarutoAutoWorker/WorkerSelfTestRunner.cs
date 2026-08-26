using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class WorkerSelfTestRunner
{
    internal static int Run()
    {
        try
        {
            VerifyFocusProjection();
            VerifyCallbackAdapter();
            VerifyLogResponseBudget();
            VerifyLatestFramePreview();
            VerifyPreviewStopRejectsInFlightFrame();
            VerifyPreviewResponseBudget();
            VerifyPreviewResponseBudgetRejection();
            VerifyTransportWriteBeforeSendGuard();
            Console.WriteLine(
                "WORKER SELF-TEST PASS: MaaNOP string focus projection; Callback adapter; "
                + "log response budget; latest-frame preview; preview response budget; "
                + "budget rejection; transport write guard");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WORKER SELF-TEST FAIL: {exception}");
            return 1;
        }
    }

    private static void VerifyFocusProjection()
    {
        var buffer = new WorkerLogBuffer();
        var adapter = new MaaRunLogAdapter((level, source, message) => buffer.Add(level, source, message));
        const string details =
            """
            {
              "name": "领取邮件",
              "task_id": 42,
              "enabled": true,
              "nested": { "value": 1 },
              "focus": {
                "Node.Action.Succeeded": "{name}: {task_id}, {enabled}, {missing}, {nested}",
                "Node.Action.Starting": { "content": "unsupported" }
              }
            }
            """;

        adapter.Handle("Node.Action.Succeeded", details);
        var entries = buffer.GetSince(0, 500).Entries;
        if (entries.Count != 1
            || entries[0].Level != "INFO"
            || entries[0].Source != ProtocolConstants.MaaNopRunLogSource
            || entries[0].Message != "领取邮件: 42, true, {missing}, {nested}")
        {
            throw new InvalidOperationException("字符串 focus 或占位符投影验证失败。 ");
        }

        buffer = new WorkerLogBuffer();
        adapter = new MaaRunLogAdapter((level, source, message) => buffer.Add(level, source, message));
        adapter.Handle("Node.Action.Starting", details);
        adapter.Handle("Node.Action.Failed", details);
        adapter.Handle("evt", "{}");
        adapter.Handle("evt", "{\"focus\":null}");
        adapter.Handle("evt", "{\"focus\":42}");
        adapter.Handle("evt", "{\"focus\":true}");
        adapter.Handle("evt", "{\"focus\":[]}");
        adapter.Handle("evt", "{\"focus\":{\"evt\":\" \"}}");
        if (buffer.GetSince(0, 500).Entries.Count != 0)
        {
            throw new InvalidOperationException("非字符串、未匹配或空 focus 不应产生运行日志。 ");
        }

        buffer = new WorkerLogBuffer();
        adapter = new MaaRunLogAdapter((level, source, message) => buffer.Add(level, source, message));
        adapter.Handle("evt", "{\"nullval\":null,\"arrval\":[1,2],\"focus\":{\"evt\":\"{nullval} {arrval}\"}}");
        entries = buffer.GetSince(0, 500).Entries;
        if (entries.Count != 1 || entries[0].Message != "{nullval} {arrval}")
        {
            throw new InvalidOperationException("null/array 占位符应保持原样不变。 ");
        }

        buffer = new WorkerLogBuffer();
        adapter = new MaaRunLogAdapter((level, source, message) => buffer.Add(level, source, message));
        adapter.Handle("Node.Action.Succeeded", "not-json");
        adapter.Handle("Node.Action.Succeeded", "still-not-json");
        entries = buffer.GetSince(0, 500).Entries;
        if (entries.Count != 1
            || entries[0].Level != "WARN"
            || entries[0].Source != "maanop.callback")
        {
            throw new InvalidOperationException("非法 JSON 应经 Adapter 产生最多一条 WARN，不逃逸异常。 ");
        }
    }

    private static void VerifyLogResponseBudget()
    {
        var buffer = new WorkerLogBuffer();
        var message = new string('x', ProtocolConstants.MaximumLogMessageBytes);
        for (var index = 0; index < 40; index++)
        {
            buffer.Add("INFO", ProtocolConstants.MaaNopRunLogSource, message);
        }

        var page = buffer.GetSince(0, 500);
        var serializedBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page, ProtocolJson.Options).Length;
        if (page.Entries.Count == 0
            || serializedBytes > ProtocolConstants.MaximumLogGetSinceResponseBytes
            || !page.HasMore)
        {
            throw new InvalidOperationException("log.getSince 响应预算验证失败。 ");
        }
    }

    private static void VerifyCallbackAdapter()
    {
        var runId = Guid.NewGuid();
        var planItemId = Guid.NewGuid();
        var buffer = new WorkerLogBuffer();
        var adapter = new MaaRunLogAdapter(
            (level, source, message) => buffer.Add(level, source, message, runId, planItemId, "Task"));
        const string details =
            """
            {
              "name": "领取邮件",
              "focus": { "Node.Action.Succeeded": "完成 {name}" }
            }
            """;

        adapter.Handle("Node.Action.Succeeded", details);
        adapter.Handle("Node.Action.Failed", details);
        adapter.Handle("Node.Action.Succeeded", "not-json");
        adapter.Handle("Node.Action.Succeeded", "still-not-json");
        var entries = buffer.GetSince(0, 500).Entries;
        if (entries.Count != 2
            || entries[0].Level != "INFO"
            || entries[0].Source != ProtocolConstants.MaaNopRunLogSource
            || entries[0].Message != "完成 领取邮件"
            || entries[0].RunId != runId
            || entries[0].PlanItemId != planItemId
            || entries[0].TaskName != "Task"
            || entries[1].Level != "WARN"
            || entries[1].Source != "maanop.callback")
        {
            throw new InvalidOperationException("Callback Adapter 输出、关联字段或诊断限频验证失败。 ");
        }

        VerifyOversizedCallback(adapter, buffer);
        VerifyConcurrentCallbacks(details);
        var failingLogAdapter = new MaaRunLogAdapter((_, _, _) => throw new IOException("scripted log failure"));
        failingLogAdapter.Handle("Node.Action.Succeeded", details);
    }

    private static void VerifyOversizedCallback(MaaRunLogAdapter adapter, WorkerLogBuffer buffer)
    {
        var oversized = new string('界', ProtocolConstants.MaximumLogMessageBytes);
        var details = System.Text.Json.JsonSerializer.Serialize(
            new { focus = new Dictionary<string, string> { ["Oversized"] = oversized } },
            ProtocolJson.Options);
        adapter.Handle("Oversized", details);
        var entry = buffer.GetSince(2, 500).Entries.Single();
        if (!entry.Truncated
            || entry.OriginalByteLength is null
            || entry.OriginalByteLength <= ProtocolConstants.MaximumLogMessageBytes)
        {
            throw new InvalidOperationException("Callback Adapter 未继承 WorkerLogEntry UTF-8 截断语义。 ");
        }
    }

    private static void VerifyConcurrentCallbacks(string details)
    {
        var buffer = new WorkerLogBuffer();
        var adapter = new MaaRunLogAdapter((level, source, message) => buffer.Add(level, source, message));
        Parallel.For(0, 32, _ => adapter.Handle("Node.Action.Succeeded", details));
        var entries = buffer.GetSince(0, 500).Entries;
        if (entries.Count != 32
            || entries.Select(entry => entry.Sequence).Distinct().Count() != 32
            || entries.Where((entry, index) => entry.Sequence != index + 1).Any())
        {
            throw new InvalidOperationException("并发 Callback 未产生唯一且单调递增的 WorkerLogEntry sequence。 ");
        }
    }

    private static void VerifyLatestFramePreview()
    {
        var runId = Guid.NewGuid();
        var sampledAtUtc = new DateTime(2026, 8, 26, 8, 0, 0, DateTimeKind.Utc);
        var failures = new List<string>();
        var source = new ScriptedPreviewFrameSource(
            () => new PreviewImageData(sampledAtUtc, 4, 3, [1, 2, 3]),
            () => new PreviewImageData(sampledAtUtc.AddMilliseconds(200), 4, 3, [1, 2, 3]),
            () => new PreviewImageData(sampledAtUtc.AddMilliseconds(400), 4, 3, [4, 5, 6]),
            () => throw new InvalidOperationException("scripted capture failure"),
            () => new PreviewImageData(sampledAtUtc.AddMilliseconds(800), 0, 3, [7]));
        var preview = new LatestFramePreview(runId, source, (_, _, message) => failures.Add(message));

        preview.Pump(sampledAtUtc);
        var first = preview.ReadLatest();
        preview.Pump(sampledAtUtc.AddMilliseconds(199));
        if (first is null
            || first.RunId != runId
            || first.Revision != 1
            || first.SampledAtUtc != sampledAtUtc
            || !first.PngBytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 })
            || source.ReadCount != 1)
        {
            throw new InvalidOperationException("Preview 首帧、sampledAtUtc 或 200ms 限频验证失败。 ");
        }

        preview.Pump(sampledAtUtc.AddMilliseconds(200));
        if (preview.ReadLatest()?.Revision != 1 || source.ReadCount != 2)
        {
            throw new InvalidOperationException("Preview 重复画面不应推进 revision。 ");
        }

        preview.Pump(sampledAtUtc.AddMilliseconds(400));
        var second = preview.ReadLatest();
        if (second?.Revision != 2
            || second.SampledAtUtc != sampledAtUtc.AddMilliseconds(400)
            || !second.PngBytes.AsSpan().SequenceEqual(new byte[] { 4, 5, 6 }))
        {
            throw new InvalidOperationException("Preview 内容变化未替换 latest frame。 ");
        }

        preview.Pump(sampledAtUtc.AddMilliseconds(600));
        preview.Pump(sampledAtUtc.AddMilliseconds(800));
        if (preview.ReadLatest()?.Revision != 2 || failures.Count != 1)
        {
            throw new InvalidOperationException("Preview 失败隔离、旧帧保留或诊断限频验证失败。 ");
        }

        preview.Stop();
        preview.Pump(sampledAtUtc.AddSeconds(31));
        if (preview.ReadLatest() is not null)
        {
            throw new InvalidOperationException("Preview stop 后未清空缓存，或在途生产者重新发布了画面。 ");
        }
    }

    private static void VerifyPreviewResponseBudget()
    {
        var pngBytes = new byte[ProtocolConstants.MaximumPreviewPngBytes];
        var response = new PreviewGetLatestResponse(
            "frame",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            DateTime.UtcNow,
            640,
            360,
            "image/png",
            pngBytes,
            null);
        var envelope = WireEnvelope.Response(ProtocolOperations.PreviewGetLatest, Guid.NewGuid(), response);
        var serializedBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ProtocolJson.Options).Length;
        if (serializedBytes > ProtocolConstants.MaximumPreviewResponseBytes
            || ProtocolConstants.MaximumPreviewResponseBytes >= ProtocolConstants.MaximumFramePayloadBytes)
        {
            throw new InvalidOperationException("Preview PNG/base64 响应预算验证失败。 ");
        }
    }

    private static void VerifyPreviewResponseBudgetRejection()
    {
        var oversizedPng = new byte[ProtocolConstants.MaximumPreviewPngBytes + 256 * 1024];
        var response = new PreviewGetLatestResponse(
            "frame", Guid.NewGuid(), Guid.NewGuid(), 1, DateTime.UtcNow,
            640, 360, "image/png", oversizedPng, null);
        var envelope = WireEnvelope.Response(ProtocolOperations.PreviewGetLatest, Guid.NewGuid(), response);
        var serializedBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            envelope, ProtocolJson.Options).Length;
        if (serializedBytes <= ProtocolConstants.MaximumPreviewResponseBytes)
        {
            throw new InvalidOperationException("超过 PNG 预算的响应应触发 2 MiB 响应预算拒绝边界。 ");
        }
    }

    private static void VerifyTransportWriteBeforeSendGuard()
    {
        var oversizedPng = new byte[3 * 1024 * 1024 + 1];
        var response = new PreviewGetLatestResponse(
            "frame", Guid.NewGuid(), Guid.NewGuid(), 1, DateTime.UtcNow,
            640, 360, "image/png", oversizedPng, null);
        var envelope = WireEnvelope.Response(ProtocolOperations.PreviewGetLatest, Guid.NewGuid(), response);
        using var stream = new MemoryStream();
        var connection = new ProtocolConnection(stream);
        try
        {
            connection.WriteAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("超过 4 MiB transport 预算的 envelope 未被拒绝。 ");
        }
        catch (ProtocolException)
        {
            // Expected: write-before-send guard rejects oversized transport payload.
        }
    }

    private static void VerifyPreviewStopRejectsInFlightFrame()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sampledAtUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc);
        var source = new BlockingPreviewFrameSource(entered, release, sampledAtUtc);
        var preview = new LatestFramePreview(Guid.NewGuid(), source, (_, _, _) => { });
        var pump = Task.Run(() => preview.Pump(sampledAtUtc));
        if (!entered.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("Preview 在途停止自检未进入 frame source。 ");
        }
        preview.Stop();
        release.Set();
        if (!pump.Wait(TimeSpan.FromSeconds(2)) || preview.ReadLatest() is not null)
        {
            throw new InvalidOperationException("Preview Stop 后发布了已经在途的旧帧。 ");
        }
    }

    private sealed class ScriptedPreviewFrameSource(params Func<PreviewImageData?>[] reads) : IPreviewFrameSource
    {
        private readonly Queue<Func<PreviewImageData?>> _reads = new(reads);

        internal int ReadCount { get; private set; }

        public PreviewImageData? ReadLatest()
        {
            ReadCount++;
            return _reads.Count == 0 ? null : _reads.Dequeue()();
        }
    }

    private sealed class BlockingPreviewFrameSource(
        ManualResetEventSlim entered,
        ManualResetEventSlim release,
        DateTime sampledAtUtc) : IPreviewFrameSource
    {
        public PreviewImageData? ReadLatest()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Preview 在途停止自检未释放 frame source。 ");
            }
            return new PreviewImageData(sampledAtUtc, 4, 3, [1, 2, 3]);
        }
    }
}
