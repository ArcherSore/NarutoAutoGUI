using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static class WorkerCoordinatorSelfTest
{
    internal static async Task RunAsync(
        AppLogger logger,
        string testDirectory,
        string projectDirectory,
        string configPath)
    {
        var stateDirectory = Path.Combine(testDirectory, "worker-coordinator");
        var workerInstanceId = Guid.NewGuid();
        var launchToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var childSessionId = checked((uint)Process.GetCurrentProcess().SessionId);
        const string runtimeProfileDigest = "self-test-runtime";
        var record = new WorkerAdmissionRecord(
            workerInstanceId,
            launchToken,
            childSessionId,
            Environment.ProcessId,
            runtimeProfileDigest,
            DateTime.UtcNow);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "worker.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options));
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("无法取得自检进程路径。 ");
        var received = new ConcurrentQueue<WorkerLogEntry>();
        var pipeName = $"NarutoAutoGUI.Worker.SelfTest.{Guid.NewGuid():N}";

        await using var coordinator = new WorkerCoordinator(
            logger,
            stateDirectory,
            executablePath,
            pipeName,
            usePipeAcl: false);
        coordinator.LogReceived += (_, entry) => received.Enqueue(entry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await coordinator.WaitForServerReadyAsync(timeout.Token);
        await VerifyRecoveryAndRetryAsync(pipeName, record, received, timeout.Token);
        await VerifyDisconnectDuringRecoveryAsync(pipeName, record, received, timeout.Token);
        await VerifySameInstanceReconnectAndTeardownAsync(coordinator, pipeName, record, received, timeout.Token);
        await VerifyWorkerInstanceReplacementAsync(
            logger, stateDirectory, executablePath, childSessionId, received, timeout.Token);
    }

    private static async Task VerifyRecoveryAndRetryAsync(
        string pipeName,
        WorkerAdmissionRecord record,
        ConcurrentQueue<WorkerLogEntry> received,
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
                "transient_failure", "scripted transient failure", retriable: true),
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
        if (!entries.Select(entry => entry.Sequence).SequenceEqual([1L, 2L, 3L, 4L, 5L]))
        {
            throw new InvalidOperationException("Coordinator gap recovery 未按 sequence 顺序发布。 ");
        }
    }

    private static async Task VerifyDisconnectDuringRecoveryAsync(
        string pipeName,
        WorkerAdmissionRecord record,
        ConcurrentQueue<WorkerLogEntry> received,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTime.UtcNow;
        var stranded = CreateEntry(7, ProtocolConstants.MaaNopRunLogSource, "recovered-after-eof", timestamp);
        await using (var disconnected = await OpenConnectionAsync(
            pipeName, record, lastLogSequence: 5, cancellationToken))
        {
            await disconnected.WriteAsync(CreateLogEvent(record.WorkerInstanceId, stranded), cancellationToken);
            _ = await ReadRequestAsync(disconnected, ProtocolOperations.LogGetSince, cancellationToken);
        }

        await using var reconnected = await OpenConnectionAsync(
            pipeName, record, lastLogSequence: 7, cancellationToken);
        var recoveryRequest = await ReadRequestAsync(reconnected, ProtocolOperations.LogGetSince, cancellationToken);
        await WriteEvictionLogPageAsync(
            reconnected, recoveryRequest, stranded, firstAvailable: 7, missingFrom: 6, missingTo: 6, cancellationToken);
        await WaitForCountAsync(received, expectedCount: 6, cancellationToken);
        if (!received.Select(entry => entry.Sequence).SequenceEqual([1L, 2L, 3L, 4L, 5L, 7L]))
        {
            throw new InvalidOperationException("Coordinator 断线重连或 eviction gap 恢复失败。 ");
        }
    }

    private static async Task VerifySameInstanceReconnectAndTeardownAsync(
        WorkerCoordinator coordinator,
        string pipeName,
        WorkerAdmissionRecord record,
        ConcurrentQueue<WorkerLogEntry> received,
        CancellationToken cancellationToken)
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
        if (received.Count != 7 || received.Last().Sequence != 8)
        {
            throw new InvalidOperationException("失效 Worker 的在途 recovery 结果仍被发布。 ");
        }
    }

    private static async Task VerifyWorkerInstanceReplacementAsync(
        AppLogger logger,
        string stateDirectory,
        string executablePath,
        uint childSessionId,
        ConcurrentQueue<WorkerLogEntry> received,
        CancellationToken cancellationToken)
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
        if (last.Sequence != 1 || last.Message != "worker-b-first")
        {
            throw new InvalidOperationException(
                "Worker B sequence 1 未被接受，或旧 Log Transport Cursor 抑制了新实例日志。 ");
        }
    }

    private static async Task<ProtocolConnection> OpenConnectionAsync(
        string pipeName,
        WorkerAdmissionRecord record,
        long lastLogSequence,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(5000, cancellationToken);
            var connection = new ProtocolConnection(pipe);
            var requestId = Guid.NewGuid();
            await connection.WriteAsync(
                WireEnvelope.Request(
                    ProtocolOperations.ConnectionOpen, requestId,
                    new ConnectionOpenRequest(
                        record.WorkerInstanceId, record.LaunchToken, "self-test",
                        record.RuntimeProfileDigest)),
                cancellationToken);
            var openResponse = await connection.ReadAsync(cancellationToken)
                               ?? throw new EndOfStreamException("Coordinator 未返回 connection.open。 ");
            if (openResponse.RequestId != requestId || openResponse.Success != true)
            {
                throw new InvalidOperationException("Coordinator self-test admission 失败。 ");
            }

            var snapshotRequest = await ReadRequestAsync(
                connection, ProtocolOperations.WorkerGetSnapshot, cancellationToken);
            await connection.WriteAsync(
                WireEnvelope.Response(
                    ProtocolOperations.WorkerGetSnapshot, snapshotRequest.RequestId!.Value,
                    new GetSnapshotResponse(CreateSnapshot(record, lastLogSequence))),
                cancellationToken);
            return connection;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static WorkerSnapshot CreateSnapshot(WorkerAdmissionRecord record, long lastLogSequence)
    {
        var available = new DependencyCheck(true, "self-test", null);
        return new WorkerSnapshot(
            ProtocolConstants.SnapshotVersion,
            DateTime.UtcNow,
            1,
            record.WorkerInstanceId,
            Environment.ProcessId,
            record.ChildSessionId,
            "self-test",
            ProtocolConstants.ProtocolVersion,
            record.RuntimeProfileDigest,
            new ProjectProvenance("self-test", "1", 1, "self-test"),
            WorkerState.Ready,
            null,
            new DependencyStatus(
                DateTime.UtcNow,
                "self-test",
                "self-test",
                available,
                available,
                available,
                available,
                available),
            RunState.Idle,
            null,
            null,
            1,
            lastLogSequence);
    }

    private static WorkerLogEntry CreateEntry(long sequence, string source, string message, DateTime timestampUtc) =>
        new(sequence, timestampUtc, "INFO", source, message, false, null, null, null, null);

    private static WireEnvelope CreateLogEvent(Guid workerInstanceId, WorkerLogEntry entry) =>
        WireEnvelope.Event(ProtocolOperations.LogEntry, new LogEntryEvent(workerInstanceId, entry));

    private static async Task<WireEnvelope> ReadRequestAsync(
        ProtocolConnection connection,
        string operation,
        CancellationToken cancellationToken)
    {
        var request = await connection.ReadAsync(cancellationToken)
                      ?? throw new EndOfStreamException($"等待 {operation} 时 Pipe 已关闭。 ");
        if (request.MessageType != ProtocolMessageTypes.Request
            || request.Operation != operation
            || request.RequestId is null)
        {
            throw new InvalidOperationException($"预期 request={operation}，实际为 {request.Operation}。 ");
        }
        return request;
    }

    private static Task WriteLogPageAsync(
        ProtocolConnection connection,
        WireEnvelope request,
        IReadOnlyList<WorkerLogEntry> entries,
        long lastSequence,
        CancellationToken cancellationToken) =>
        connection.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.LogGetSince,
                request.RequestId!.Value,
                new LogGetSinceResponse(entries, 500, 1, lastSequence, false, false, null, null)),
            cancellationToken);

    private static Task WriteEvictionLogPageAsync(
        ProtocolConnection connection,
        WireEnvelope request,
        WorkerLogEntry entry,
        long firstAvailable,
        long missingFrom,
        long missingTo,
        CancellationToken cancellationToken) =>
        connection.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.LogGetSince,
                request.RequestId!.Value,
                new LogGetSinceResponse(
                    [entry], 500, firstAvailable, entry.Sequence, false, true, missingFrom, missingTo)),
            cancellationToken);

    private static async Task WaitForCountAsync(
        ConcurrentQueue<WorkerLogEntry> entries,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        while (entries.Count < expectedCount)
        {
            await Task.Delay(20, cancellationToken);
        }
    }
}
