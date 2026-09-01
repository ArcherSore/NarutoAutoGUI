using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static class WorkerCoordinatorSelfTest
{
    internal static async Task RunAsync(
        AppLogger logger, string testDirectory, string projectDirectory, string configPath)
    {
        var stateDirectory = Path.Combine(testDirectory, "worker-coordinator");
        var workerInstanceId = Guid.NewGuid();
        var launchToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var childSessionId = checked((uint)Process.GetCurrentProcess().SessionId);
        const string runtimeProfileDigest = "self-test-runtime";
        var record = new WorkerAdmissionRecord(
            workerInstanceId, launchToken, childSessionId, Environment.ProcessId,
            runtimeProfileDigest, DateTime.UtcNow);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "worker.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options));
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("无法取得自检进程路径。 ");
        var received = new ConcurrentQueue<WorkerLogEntry>();
        var pipeName = $"NarutoAutoGUI.Worker.SelfTest.{Guid.NewGuid():N}";

        await using var coordinator = new WorkerCoordinator(
            logger, stateDirectory, executablePath, pipeName, usePipeAcl: false);
        coordinator.LogReceived += (_, entry) => received.Enqueue(entry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await coordinator.WaitForServerReadyAsync(timeout.Token);
        var project = ProjectPlanModule.Open(projectDirectory, configPath);
        var activeRun = CreateActiveRun(project.CreateRunStartAttempt());
        await VerifyPreviewRequestAsync(coordinator, pipeName, record, activeRun, timeout.Token);
        await VerifyRecoveryAndRetryAsync(pipeName, record, received, timeout.Token);
        await VerifyDisconnectDuringRecoveryAsync(pipeName, record, received, timeout.Token);
        await VerifySameInstanceReconnectAndTeardownAsync(coordinator, pipeName, record, received, timeout.Token);
        await VerifyWorkerInstanceReplacementAsync(
            logger, stateDirectory, executablePath, childSessionId, received, timeout.Token);
    }

    private static async Task VerifyPreviewRequestAsync(
        WorkerCoordinator coordinator, string pipeName, WorkerAdmissionRecord record,
        RunSnapshot activeRun, CancellationToken cancellationToken)
    {
        await using var pipe = await OpenConnectionAsync(
            pipeName, record, lastLogSequence: 0, cancellationToken, activeRun);
        await WaitForActiveRunAsync(coordinator, activeRun.RunId, cancellationToken);

        var firstResponseTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 0, cancellationToken);
        var firstRequest = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        var firstData = ProtocolJson.Deserialize<PreviewGetLatestRequest>(firstRequest.Data);
        if (firstData.RunId != activeRun.RunId || firstData.AfterRevision != 0) {
            throw new InvalidOperationException("Coordinator Preview 首次请求 cursor 非法。 ");
        }
        var sampledAtUtc = DateTime.UtcNow;
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, firstRequest.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "frame", record.WorkerInstanceId, activeRun.RunId, 1, sampledAtUtc,
                    4, 3, "image/png", [1, 2, 3], null)),
            cancellationToken);
        var firstResponse = await firstResponseTask;
        if (firstResponse.Revision != 1 || firstResponse.SampledAtUtc != sampledAtUtc) {
            throw new InvalidOperationException("Coordinator Preview frame 响应验证失败。 ");
        }

        var unchangedTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 1, cancellationToken);
        var unchangedRequest = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        var unchangedData = ProtocolJson.Deserialize<PreviewGetLatestRequest>(unchangedRequest.Data);
        if (unchangedData.AfterRevision != 1) {
            throw new InvalidOperationException("Coordinator 未携带最新 Preview revision。 ");
        }
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, unchangedRequest.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "not_modified", record.WorkerInstanceId, activeRun.RunId, 1,
                    null, null, null, null, null, null)),
            cancellationToken);
        if ((await unchangedTask).Disposition != "not_modified") {
            throw new InvalidOperationException("Coordinator Preview not_modified 验证失败。 ");
        }

        var staleIdentityTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 1, cancellationToken);
        var staleIdentityRequest = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, staleIdentityRequest.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "frame", Guid.NewGuid(), activeRun.RunId, 2, DateTime.UtcNow,
                    4, 3, "image/png", [4, 5, 6], null)),
            cancellationToken);
        try {
            _ = await staleIdentityTask;
            throw new InvalidOperationException("Coordinator 未拒绝错误 Worker Instance 的 Preview。 ");
        } catch (ProtocolException) {
            // Expected: stale frame identity is fail-closed before GUI display.
        }

        var unavailMismatchTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 1, cancellationToken);
        var unavailMismatchReq = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, unavailMismatchReq.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "unavailable", record.WorkerInstanceId, Guid.NewGuid(), 0,
                    null, null, null, null, null, "run_mismatch")),
            cancellationToken);
        try {
            _ = await unavailMismatchTask;
            throw new InvalidOperationException("Coordinator 未拒绝错误 runId 的 unavailable Preview。 ");
        } catch (ProtocolException) {
            // Expected: unavailable response carrying a foreign runId is fail-closed.
        }

        var unavailNullTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 1, cancellationToken);
        var unavailNullReq = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, unavailNullReq.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "unavailable", record.WorkerInstanceId, null, 0,
                    null, null, null, null, null, "no_active_run")),
            cancellationToken);
        var unavailNullResponse = await unavailNullTask;
        if (unavailNullResponse.Disposition != "unavailable" || unavailNullResponse.Reason != "no_active_run") {
            throw new InvalidOperationException("Coordinator 未接受 null runId 的合法 unavailable Preview。 ");
        }

        var staleRunTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 1, cancellationToken);
        var staleRunReq = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, staleRunReq.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "frame", record.WorkerInstanceId, Guid.NewGuid(), 2, DateTime.UtcNow,
                    4, 3, "image/png", [7, 8, 9], null)),
            cancellationToken);
        try {
            _ = await staleRunTask;
            throw new InvalidOperationException("Coordinator 未拒绝错误 runId 的 frame Preview。 ");
        } catch (ProtocolException) {
            // Expected: frame response carrying a foreign runId is fail-closed.
        }

        using var cancelledRequest = new CancellationTokenSource();
        var cancelledTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 1, cancelledRequest.Token);
        var cancelledEnvelope = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        cancelledRequest.Cancel();
        try {
            _ = await cancelledTask;
            throw new InvalidOperationException("Coordinator Preview 取消请求未取消。 ");
        } catch (OperationCanceledException) {
            // Expected: UI polling cancellation only abandons this caller's wait.
        }
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, cancelledEnvelope.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "not_modified", record.WorkerInstanceId, activeRun.RunId, 1,
                    null, null, null, null, null, null)),
            cancellationToken);

        var afterCancellationTask = coordinator.GetLatestPreviewAsync(activeRun.RunId, 1, cancellationToken);
        var afterCancellationRequest = await ReadRequestAsync(pipe, ProtocolOperations.PreviewGetLatest, cancellationToken);
        await pipe.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.PreviewGetLatest, afterCancellationRequest.RequestId!.Value,
                new PreviewGetLatestResponse(
                    "not_modified", record.WorkerInstanceId, activeRun.RunId, 1,
                    null, null, null, null, null, null)),
            cancellationToken);
        if ((await afterCancellationTask).Disposition != "not_modified") {
            throw new InvalidOperationException("迟到 Preview response 导致 Coordinator IPC 失效。 ");
        }
    }

    private static async Task VerifyRecoveryAndRetryAsync(
        string pipeName, WorkerAdmissionRecord record, ConcurrentQueue<WorkerLogEntry> received,
        CancellationToken cancellationToken)
    {
        await using var pipe = await OpenConnectionAsync(pipeName, record, lastLogSequence: 0, cancellationToken);
        var timestamp = DateTime.UtcNow;
        var diagnostic = CreateEntry(1, "runtime.task", "diagnostic", timestamp);
        var firstRunLog = CreateEntry(2, ProtocolConstants.MaaNopRunLogSource, "first", timestamp.AddMilliseconds(1));
        var secondRunLog = CreateEntry(3, ProtocolConstants.MaaNopRunLogSource, "second", timestamp.AddMilliseconds(2));
        var thirdRunLog = CreateEntry(4, ProtocolConstants.MaaNopRunLogSource, "third", timestamp.AddMilliseconds(3));
        var fourthRunLog = CreateEntry(5, ProtocolConstants.MaaNopRunLogSource, "fourth", timestamp.AddMilliseconds(4));

        await pipe.WriteAsync(CreateLogEvent(record.WorkerInstanceId, firstRunLog), cancellationToken);
        var firstRequest = await ReadRequestAsync(pipe, ProtocolOperations.LogGetSince, cancellationToken);
        await pipe.WriteAsync(
            WireEnvelope.Failure(
                ProtocolOperations.LogGetSince, firstRequest.RequestId!.Value,
                "transient_failure", "scripted transient failure"),
            cancellationToken);

        var retryRequest = await ReadRequestAsync(pipe, ProtocolOperations.LogGetSince, cancellationToken);
        await pipe.WriteAsync(CreateLogEvent(record.WorkerInstanceId, secondRunLog), cancellationToken);
        await pipe.WriteAsync(CreateLogEvent(record.WorkerInstanceId, fourthRunLog), cancellationToken);
        await WriteLogPageAsync(pipe, retryRequest, [diagnostic, firstRunLog], lastSequence: 5, cancellationToken);
        var finalRequest = await ReadRequestAsync(pipe, ProtocolOperations.LogGetSince, cancellationToken);
        await WriteLogPageAsync(
            pipe, finalRequest, [secondRunLog, thirdRunLog, fourthRunLog], lastSequence: 5, cancellationToken);
        await WaitForCountAsync(received, expectedCount: 5, cancellationToken);

        var entries = received.ToArray();
        if (!entries.Select(entry => entry.Sequence).SequenceEqual([1L, 2L, 3L, 4L, 5L])) {
            throw new InvalidOperationException("Coordinator gap recovery 未按 sequence 顺序发布。 ");
        }
    }

    private static async Task VerifyDisconnectDuringRecoveryAsync(
        string pipeName, WorkerAdmissionRecord record, ConcurrentQueue<WorkerLogEntry> received,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTime.UtcNow;
        var stranded = CreateEntry(7, ProtocolConstants.MaaNopRunLogSource, "recovered-after-eof", timestamp);
        await using (var disconnected = await OpenConnectionAsync(
            pipeName, record, lastLogSequence: 5, cancellationToken)) {
            await disconnected.WriteAsync(CreateLogEvent(record.WorkerInstanceId, stranded), cancellationToken);
            _ = await ReadRequestAsync(disconnected, ProtocolOperations.LogGetSince, cancellationToken);
        }

        await using var reconnected = await OpenConnectionAsync(
            pipeName, record, lastLogSequence: 7, cancellationToken);
        var recoveryRequest = await ReadRequestAsync(reconnected, ProtocolOperations.LogGetSince, cancellationToken);
        await WriteEvictionLogPageAsync(
            reconnected, recoveryRequest, stranded, firstAvailable: 7, missingFrom: 6, missingTo: 6, cancellationToken);
        await WaitForCountAsync(received, expectedCount: 6, cancellationToken);
        if (!received.Select(entry => entry.Sequence).SequenceEqual([1L, 2L, 3L, 4L, 5L, 7L])) {
            throw new InvalidOperationException("Coordinator 断线重连或 eviction gap 恢复失败。 ");
        }
    }

    private static async Task VerifySameInstanceReconnectAndTeardownAsync(
        WorkerCoordinator coordinator, string pipeName, WorkerAdmissionRecord record,
        ConcurrentQueue<WorkerLogEntry> received, CancellationToken cancellationToken)
    {
        await using var pipe = await OpenConnectionAsync(pipeName, record, lastLogSequence: 7, cancellationToken);
        var timestamp = DateTime.UtcNow;
        var contiguous = CreateEntry(8, ProtocolConstants.MaaNopRunLogSource, "reconnected", timestamp);
        await pipe.WriteAsync(CreateLogEvent(record.WorkerInstanceId, contiguous), cancellationToken);
        await WaitForCountAsync(received, expectedCount: 7, cancellationToken);

        var stranded = CreateEntry(10, ProtocolConstants.MaaNopRunLogSource, "must-not-publish", timestamp);
        await pipe.WriteAsync(CreateLogEvent(record.WorkerInstanceId, stranded), cancellationToken);
        var recoveryRequest = await ReadRequestAsync(pipe, ProtocolOperations.LogGetSince, cancellationToken);
        coordinator.ChildSessionEnded();
        var missing = CreateEntry(9, ProtocolConstants.MaaNopRunLogSource, "also-hidden", timestamp);
        await WriteLogPageAsync(pipe, recoveryRequest, [missing, stranded], lastSequence: 10, cancellationToken);
        await Task.Delay(200, cancellationToken);
        if (received.Count != 7 || received.Last().Sequence != 8) {
            throw new InvalidOperationException("失效 Worker 的在途 recovery 结果仍被发布。 ");
        }
    }

    private static async Task VerifyWorkerInstanceReplacementAsync(
        AppLogger logger, string stateDirectory, string executablePath, uint childSessionId,
        ConcurrentQueue<WorkerLogEntry> received, CancellationToken cancellationToken)
    {
        var workerBId = Guid.NewGuid();
        var launchTokenB = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var recordB = new WorkerAdmissionRecord(
            workerBId, launchTokenB, childSessionId, Environment.ProcessId,
            "self-test-runtime", DateTime.UtcNow);
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "worker.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(recordB, ProtocolJson.Options));

        var pipeNameB = $"NarutoAutoGUI.Worker.SelfTest.{Guid.NewGuid():N}";
        await using var coordinatorB = new WorkerCoordinator(
            logger, stateDirectory, executablePath, pipeNameB, usePipeAcl: false);
        coordinatorB.LogReceived += (_, entry) => received.Enqueue(entry);
        using var timeoutB = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await coordinatorB.WaitForServerReadyAsync(timeoutB.Token);

        await using var pipeB = await OpenConnectionAsync(pipeNameB, recordB, 0, timeoutB.Token);
        var entry = CreateEntry(1, ProtocolConstants.MaaNopRunLogSource, "worker-b-first", DateTime.UtcNow);
        await pipeB.WriteAsync(CreateLogEvent(recordB.WorkerInstanceId, entry), timeoutB.Token);
        await WaitForCountAsync(received, expectedCount: 8, timeoutB.Token);

        var last = received.Last();
        if (last.Sequence != 1 || last.Message != "worker-b-first") {
            throw new InvalidOperationException(
                "Worker B sequence 1 未被接受，或旧 Log Transport Cursor 抑制了新实例日志。 ");
        }
    }

    private static async Task<ProtocolConnection> OpenConnectionAsync(
        string pipeName, WorkerAdmissionRecord record, long lastLogSequence,
        CancellationToken cancellationToken, RunSnapshot? activeRun = null)
    {
        var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try {
            await pipe.ConnectAsync(5000, cancellationToken);
            var connection = new ProtocolConnection(pipe);
            var requestId = Guid.NewGuid();
            await connection.WriteAsync(
                WireEnvelope.Request(
                    ProtocolOperations.ConnectionOpen, requestId,
                    new ConnectionOpenRequest(
                        record.WorkerInstanceId, record.LaunchToken, record.RuntimeProfileDigest)),
                cancellationToken);
            var openResponse = await connection.ReadAsync(cancellationToken)
                               ?? throw new EndOfStreamException("Coordinator 未返回 connection.open。 ");
            if (openResponse.RequestId != requestId || openResponse.Success != true) {
                throw new InvalidOperationException("Coordinator self-test admission 失败。 ");
            }

            var snapshotRequest = await ReadRequestAsync(
                connection, ProtocolOperations.WorkerGetSnapshot, cancellationToken);
            await connection.WriteAsync(
                WireEnvelope.Response(
                    ProtocolOperations.WorkerGetSnapshot, snapshotRequest.RequestId!.Value,
                    new GetSnapshotResponse(CreateSnapshot(record, lastLogSequence, activeRun))),
                cancellationToken);
            return connection;
        } catch {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static WorkerSnapshot CreateSnapshot(
        WorkerAdmissionRecord record, long lastLogSequence, RunSnapshot? activeRun = null)
    {
        var available = new DependencyCheck(true, "self-test", null);
        return new WorkerSnapshot(
            ProtocolConstants.SnapshotVersion, DateTime.UtcNow, 1,
            record.WorkerInstanceId, Environment.ProcessId, record.ChildSessionId, "self-test",
            ProtocolConstants.ProtocolVersion, record.RuntimeProfileDigest,
            new ProjectProvenance("self-test", "1", 1, "self-test"),
            WorkerState.Ready, null,
            new DependencyStatus(DateTime.UtcNow, "self-test", "self-test", available, available, available, available, available),
            activeRun?.State ?? RunState.Idle, activeRun, null, 1, lastLogSequence);
    }

    private static RunSnapshot CreateActiveRun(RunStartAttempt attempt)
    {
        var startedAtUtc = DateTime.UtcNow;
        var item = attempt.Plan.Items.Single();
        var itemSnapshot = new PlanItemSnapshot(
            item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry,
            item.ResolvedOptions, item.PipelineOverride, PlanItemState.Running,
            startedAtUtc, null, null, null, null);
        return new RunSnapshot(
            attempt.RunId, attempt.PlanDigest, RunState.Running, attempt.Plan.CreatedAtUtc,
            startedAtUtc, null, null, item.PlanItemId, 0,
            attempt.Plan, [itemSnapshot], null, null);
    }

    private static async Task WaitForActiveRunAsync(
        WorkerCoordinator coordinator, Guid runId, CancellationToken cancellationToken)
    {
        while (true) {
            var snapshot = coordinator.Snapshot;
            if (snapshot.Observation == WorkerObservation.Connected && snapshot.SnapshotFresh
                && snapshot.WorkerSnapshot?.ActiveRun?.RunId == runId) {
                return;
            }
            await Task.Delay(20, cancellationToken);
        }
    }

    private static WorkerLogEntry CreateEntry(long sequence, string source, string message, DateTime timestampUtc) =>
        new(sequence, timestampUtc, "INFO", source, message, false, null, null, null, null);

    private static WireEnvelope CreateLogEvent(Guid workerInstanceId, WorkerLogEntry entry) =>
        WireEnvelope.Event(ProtocolOperations.LogEntry, new LogEntryEvent(workerInstanceId, entry));

    private static async Task<WireEnvelope> ReadRequestAsync(
        ProtocolConnection connection, string operation, CancellationToken cancellationToken)
    {
        var request = await connection.ReadAsync(cancellationToken)
                      ?? throw new EndOfStreamException($"等待 {operation} 时 Pipe 已关闭。 ");
        if (request.MessageType != ProtocolMessageTypes.Request || request.Operation != operation
            || request.RequestId is null) {
            throw new InvalidOperationException($"预期 request={operation}，实际为 {request.Operation}。 ");
        }
        return request;
    }

    private static Task WriteLogPageAsync(
        ProtocolConnection connection, WireEnvelope request, IReadOnlyList<WorkerLogEntry> entries,
        long lastSequence, CancellationToken cancellationToken) =>
        connection.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.LogGetSince, request.RequestId!.Value,
                new LogGetSinceResponse(entries, 500, 1, lastSequence, false, false, null, null)),
            cancellationToken);

    private static Task WriteEvictionLogPageAsync(
        ProtocolConnection connection, WireEnvelope request, WorkerLogEntry entry,
        long firstAvailable, long missingFrom, long missingTo, CancellationToken cancellationToken) =>
        connection.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.LogGetSince, request.RequestId!.Value,
                new LogGetSinceResponse(
                    [entry], 500, firstAvailable, entry.Sequence, false, true, missingFrom, missingTo)),
            cancellationToken);

    private static async Task WaitForCountAsync(
        ConcurrentQueue<WorkerLogEntry> entries, int expectedCount, CancellationToken cancellationToken)
    {
        while (entries.Count < expectedCount) {
            await Task.Delay(20, cancellationToken);
        }
    }
}
