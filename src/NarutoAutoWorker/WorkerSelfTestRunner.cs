using MaaFramework.Binding;
using MaaFramework.Binding.Notification;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class WorkerSelfTestRunner
{
    internal static int Run()
    {
        PreviewSelfTests.Run();
        try {
            VerifyFocusProjection();
            VerifyCallbackAdapter();
            VerifyLogResponseBudget();
            VerifyTransportWriteBeforeSendGuard();
            VerifyAgentExecutableResolution();
            VerifyAcceptedStopWinsTerminalRace();
            VerifyStopCleanupRaceAsync().GetAwaiter().GetResult();
            VerifyTaskerTaskCallbackCompletion();
            Console.WriteLine(
                "WORKER SELF-TEST PASS: MaaNOP string focus projection; Callback adapter; "
                + "log response budget; preview shared buffer; "
                + "budget rejection; transport write guard; agent executable resolution; "
                + "accepted stop wins terminal race; stop/cleanup serialization; "
                + "Tasker.Task callback completion");
            return 0;
        } catch (Exception exception) {
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
        if (entries.Count != 1 || entries[0].Level != "INFO"
            || entries[0].Source != ProtocolConstants.MaaNopRunLogSource
            || entries[0].Message != "领取邮件: 42, true, {missing}, {nested}") {
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
        if (buffer.GetSince(0, 500).Entries.Count != 0) {
            throw new InvalidOperationException("非字符串、未匹配或空 focus 不应产生运行日志。 ");
        }

        buffer = new WorkerLogBuffer();
        adapter = new MaaRunLogAdapter((level, source, message) => buffer.Add(level, source, message));
        adapter.Handle("evt", "{\"nullval\":null,\"arrval\":[1,2],\"focus\":{\"evt\":\"{nullval} {arrval}\"}}");
        entries = buffer.GetSince(0, 500).Entries;
        if (entries.Count != 1 || entries[0].Message != "{nullval} {arrval}") {
            throw new InvalidOperationException("null/array 占位符应保持原样不变。 ");
        }

        buffer = new WorkerLogBuffer();
        adapter = new MaaRunLogAdapter((level, source, message) => buffer.Add(level, source, message));
        adapter.Handle("Node.Action.Succeeded", "not-json");
        adapter.Handle("Node.Action.Succeeded", "still-not-json");
        entries = buffer.GetSince(0, 500).Entries;
        if (entries.Count != 1 || entries[0].Level != "WARN" || entries[0].Source != "maanop.callback") {
            throw new InvalidOperationException("非法 JSON 应经 Adapter 产生最多一条 WARN，不逃逸异常。 ");
        }
    }

    private static void VerifyLogResponseBudget()
    {
        var buffer = new WorkerLogBuffer();
        var message = new string('x', ProtocolConstants.MaximumLogMessageBytes);
        for (var index = 0; index < 40; index++) {
            buffer.Add("INFO", ProtocolConstants.MaaNopRunLogSource, message);
        }

        var page = buffer.GetSince(0, 500);
        var serializedBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page, ProtocolJson.Options).Length;
        if (page.Entries.Count == 0
            || serializedBytes > ProtocolConstants.MaximumLogGetSinceResponseBytes || !page.HasMore) {
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
        if (entries.Count != 2 || entries[0].Level != "INFO"
            || entries[0].Source != ProtocolConstants.MaaNopRunLogSource
            || entries[0].Message != "完成 领取邮件" || entries[0].RunId != runId
            || entries[0].PlanItemId != planItemId || entries[0].TaskName != "Task"
            || entries[1].Level != "WARN" || entries[1].Source != "maanop.callback") {
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
        if (!entry.Truncated || entry.OriginalByteLength is null
            || entry.OriginalByteLength <= ProtocolConstants.MaximumLogMessageBytes) {
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
            || entries.Where((entry, index) => entry.Sequence != index + 1).Any()) {
            throw new InvalidOperationException("并发 Callback 未产生唯一且单调递增的 WorkerLogEntry sequence。 ");
        }
    }




    private static void VerifyTransportWriteBeforeSendGuard()
    {
        var oversizedPng = new byte[3 * 1024 * 1024 + 1];
        var envelope = WireEnvelope.Response(ProtocolOperations.LogGetSince, Guid.NewGuid(),
            new { message = new string('x', ProtocolConstants.MaximumFramePayloadBytes + 1) });
        using var stream = new MemoryStream();
        var connection = new ProtocolConnection(stream);
        try {
            connection.WriteAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("超过 4 MiB transport 预算的 envelope 未被拒绝。 ");
        } catch (ProtocolException) {
            // Expected: write-before-send guard rejects oversized transport payload.
        }
    }


    private static void VerifyAgentExecutableResolution()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "WorkerSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try {
            var pythonSubdir = Path.Combine(tempDirectory, "python");
            Directory.CreateDirectory(pythonSubdir);
            var pythonExePath = Path.Combine(pythonSubdir, "python.exe");
            File.WriteAllText(pythonExePath, "dummy");

            var agentRelative = new AgentDefinition("./python/python.exe", ["./agent/main.py"], tempDirectory);
            var resolvedRelative = DependencyProbe.ResolveAgentExecutablePath(agentRelative);
            if (!string.Equals(resolvedRelative, pythonExePath, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(
                    $"./python/python.exe child_exec 解析失败：expected={pythonExePath}, actual={resolvedRelative}。 ");
            }

            var agentSubpath = new AgentDefinition("python/python.exe", ["./agent/main.py"], tempDirectory);
            var resolvedSubpath = DependencyProbe.ResolveAgentExecutablePath(agentSubpath);
            if (!string.Equals(resolvedSubpath, pythonExePath, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(
                    $"python/python.exe child_exec 解析失败：expected={pythonExePath}, actual={resolvedSubpath}。 ");
            }

            var agentAbsolute = new AgentDefinition(pythonExePath, ["./agent/main.py"], tempDirectory);
            var resolvedAbsolute = DependencyProbe.ResolveAgentExecutablePath(agentAbsolute);
            if (!string.Equals(resolvedAbsolute, pythonExePath, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(
                    $"绝对路径 child_exec 解析失败：expected={pythonExePath}, actual={resolvedAbsolute}。 ");
            }
        } finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void VerifyAcceptedStopWinsTerminalRace()
    {
        var terminalOutcomes = new[] {
            RuntimeExecutionOutcome.Succeeded,
            RuntimeExecutionOutcome.Failed,
            RuntimeExecutionOutcome.Cancelled,
            RuntimeExecutionOutcome.CleanupFailed
        };
        if (terminalOutcomes.Any(outcome => WorkerHost.ResolveFinalRunState(outcome, wasStopping: true)
                                            != RunState.Cancelled)) {
            throw new InvalidOperationException("停止已接受后，Run 终态必须由停止语义决定为 Cancelled。 ");
        }
        if (WorkerHost.ResolveFinalRunState(RuntimeExecutionOutcome.Succeeded, wasStopping: false)
            != RunState.Succeeded
            || WorkerHost.ResolveFinalRunState(RuntimeExecutionOutcome.Failed, wasStopping: false) != RunState.Failed
            || WorkerHost.ResolveFinalRunState(RuntimeExecutionOutcome.Cancelled, wasStopping: false)
            != RunState.Cancelled
            || WorkerHost.ResolveFinalRunState(RuntimeExecutionOutcome.CleanupFailed, wasStopping: false)
            != RunState.Failed) {
            throw new InvalidOperationException("未停止 Run 的既有终态映射发生变化。 ");
        }
    }

    private static async Task VerifyStopCleanupRaceAsync()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var warnings = new List<string>();
        var execution = new WorkerRuntimeExecution(
            null!, Guid.NewGuid(), null!, 0, (_, _, message) => warnings.Add(message), () => { });
        var cleanupMethod = typeof(WorkerRuntimeExecution).GetMethod("CleanupAsync", flags)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cleanup = (Task<(bool Success, string? Error)>)cleanupMethod.Invoke(
            execution, [CancellationToken.None])!;
        var result = await cleanup.WaitAsync(timeout.Token);
        var stop = execution.StopAsync(timeout.Token);
        await stop.WaitAsync(timeout.Token);
        await execution.StopAsync(timeout.Token);
        if (!result.Success || warnings.Count != 0) {
            throw new InvalidOperationException("清理后重复 Stop 不应访问 Tasker 或产生告警。");
        }

        // Stop wins first, but readiness fails: cleanup must retain the unconfirmed context.
        execution = new WorkerRuntimeExecution(
            null!, Guid.NewGuid(), null!, 0, (_, _, _) => { }, () => { });
        var ready = (TaskCompletionSource<MaaFramework.Binding.MaaTasker>)typeof(WorkerRuntimeExecution)
            .GetField("_taskerReady", flags)!.GetValue(execution)!;
        stop = execution.StopAsync(timeout.Token);
        cleanup = (Task<(bool Success, string? Error)>)cleanupMethod.Invoke(execution, [CancellationToken.None])!;
        if (cleanup.IsCompleted) {
            throw new InvalidOperationException("Stop 确认完成前不得进入清理。 ");
        }
        ready.SetException(new InvalidOperationException("scripted readiness failure"));
        try {
            await stop;
            throw new InvalidOperationException("未确认停止时不应返回成功。 ");
        } catch (StopConfirmationException) {
        }
        result = await cleanup.WaitAsync(timeout.Token);
        if (result.Success) {
            throw new InvalidOperationException("Stop 未确认时必须保留 execution context。 ");
        }
        var executionResult = await execution.ExecuteAsync(timeout.Token);
        if (executionResult.Outcome != RuntimeExecutionOutcome.StopTimedOut) {
            throw new InvalidOperationException("Stop 未确认时不得被清理结果覆盖为普通终态。 ");
        }
    }

    private static void VerifyTaskerTaskCallbackCompletion()
    {
        var manifest = new LaunchManifest(
            ProtocolConstants.LaunchContextVersion, Guid.NewGuid(), "dummy-digest", "C:\\dummy",
            new ProjectProvenance("test", "1.0", 1, "dummy"),
            new Win32ControllerDefinition("test", "class", "window", "Cache", "Send", "Send"),
            new[] { new ResourceDefinition("test", new[] { "C:\\dummy" }) },
            new AgentDefinition("python.exe", new[] { "agent.py" }, "C:\\dummy"));
        var item = new RunPlanItem(
            Guid.NewGuid(), "TestTask", "Test", "TestEntry", default, default);

        var runningReported = false;
        var execution = new WorkerRuntimeExecution(
            manifest, Guid.NewGuid(), item, 0,
            (_, _, _) => { }, () => runningReported = true);

        execution.OnTaskerCallback(null, new MaaCallbackEventArgs(
            MaaMsg.Tasker.Task.Starting, "{}", MaaHandleType.Tasker));
        if (!runningReported) {
            throw new InvalidOperationException("Tasker.Task.Starting 未触发 ReportRunningOnce。 ");
        }

        execution.OnTaskerCallback(null, new MaaCallbackEventArgs(
            MaaMsg.Tasker.Task.Succeeded, "{}", MaaHandleType.Tasker));
        if (execution.TaskCompletion.Status != TaskStatus.RanToCompletion
            || !execution.TaskCompletion.Result.IsSucceeded()) {
            throw new InvalidOperationException("Tasker.Task.Succeeded 未将 _taskCompleted 完成为 Succeeded。 ");
        }

        var failedExecution = new WorkerRuntimeExecution(
            manifest, Guid.NewGuid(), item, 0,
            (_, _, _) => { }, () => { });
        failedExecution.OnTaskerCallback(null, new MaaCallbackEventArgs(
            MaaMsg.Tasker.Task.Failed, "{}", MaaHandleType.Tasker));
        if (failedExecution.TaskCompletion.Status != TaskStatus.RanToCompletion
            || !failedExecution.TaskCompletion.Result.IsFailed()) {
            throw new InvalidOperationException("Tasker.Task.Failed 未将 _taskCompleted 完成为 Failed。 ");
        }

        execution.OnTaskerCallback(null, new MaaCallbackEventArgs(
            MaaMsg.Tasker.Task.Failed, "{}", MaaHandleType.Tasker));
        if (!execution.TaskCompletion.Result.IsSucceeded()) {
            throw new InvalidOperationException("重复 terminal callback 不应覆盖首次终态。 ");
        }
    }


}
