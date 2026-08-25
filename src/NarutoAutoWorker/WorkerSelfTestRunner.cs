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
            Console.WriteLine(
                "WORKER SELF-TEST PASS: MaaNOP string focus projection; Callback adapter; log response budget");
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
}
