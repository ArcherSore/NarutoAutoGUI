using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed class WorkerHost : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    // Guards only the current connection's event sender; Worker and Run state are owned by RunSupervisor.
    private readonly object _eventsGate = new();
    private readonly WorkerArguments _arguments;
    private readonly LaunchManifest _manifest;
    private readonly WorkerLogBuffer _logs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private WorkerEventSender? _events;
    private readonly WorkerPreviewService _preview;
    private readonly RunSupervisor _supervisor;
    private readonly Func<CancellationToken, Task<(DependencyStatus Status, StructuredReason? Reason)>> _checkReadiness;
    private Guid _connectionId;

    // The execution factory and readiness check default to MaaFramework and the dependency probe; self-tests replace
    // them so Run rules can be exercised without native runtime, a game window or Python.
    internal WorkerHost(
        WorkerArguments arguments, LaunchManifest manifest, IPreviewCaptureSource? previewSource = null,
        Func<Guid, RunPlanItem, Action, IPlanItemExecution>? createExecution = null,
        Func<CancellationToken, Task<(DependencyStatus Status, StructuredReason? Reason)>>? checkReadiness = null)
    {
        _arguments = arguments;
        _manifest = manifest;
        _checkReadiness = checkReadiness ?? CheckReadinessAsync;
        _supervisor = new RunSupervisor(manifest, _logs, createExecution ?? CreateRuntimeExecution,
            (level, source, message, runId) => Log(level, source, message, runId), PublishState, _shutdown.Token);
        var sessionId = (uint)Process.GetCurrentProcess().SessionId;
        _preview = new WorkerPreviewService(manifest.WorkerInstanceId, sessionId,
            previewSource ?? new MaaPreviewCaptureSource(manifest, sessionId),
            (level, source, message) => Log(level, source, message));
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var initialization = InitializeAsync(linked.Token);
        Log("INFO", "worker.lifecycle", $"NarutoAutoWorker 启动，instance={_manifest.WorkerInstanceId}。 ");

        while (!linked.IsCancellationRequested) {
            try {
                await ConnectAndServeAsync(linked.Token);
            } catch (OperationCanceledException) when (linked.IsCancellationRequested) {
                break;
            } catch (Exception exception) when (exception is IOException
                                                     or TimeoutException
                                                     or ProtocolException
                                                     or UnauthorizedAccessException) {
                Log("WARN", "ipc.lifecycle", $"IPC 断开：{exception.GetBaseException().Message}");
                await Task.Delay(ReconnectDelay, linked.Token);
            }
        }

        try {
            await initialization;
        } catch (OperationCanceledException) when (linked.IsCancellationRequested) {
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var (status, reason) = await _checkReadiness(cancellationToken);
        _supervisor.CompleteInitialization(status, reason);
        if (reason is null) {
            Log(
                "INFO",
                "worker.readiness",
                $"Dependency Readiness=Ready；Binding={status.MaaFrameworkBindingVersion}；" +
                $"Runtime={status.MaaFrameworkRuntimeVersion}；Python={status.Python.Value}。 ");
        } else {
            Log("ERROR", "worker.readiness", $"Dependency Readiness=NotReady：{reason.Code} - {reason.Message}");
        }
    }

    private async Task<(DependencyStatus Status, StructuredReason? Reason)> CheckReadinessAsync(
        CancellationToken cancellationToken)
    {
        // Apply process-wide options before Ready admits preview controllers or task execution.
        StructuredReason? optionsFailure = null;
        try {
            WorkerFrameworkOptions.Apply(_manifest.ProjectRoot, _manifest.FrameworkOptions,
                (level, source, message) => Log(level, source, message));
        } catch (Exception exception) {
            optionsFailure = new StructuredReason("FrameworkOptionsFailed", exception.GetBaseException().Message);
        }
        return optionsFailure is null
            ? await DependencyProbe.RunAsync(_manifest, cancellationToken)
            : (_supervisor.GetSnapshot().DependencyStatus, optionsFailure);
    }

    private async Task ConnectAndServeAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeIdentity.ForCurrentUser(),
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, cancellationToken);
        await using var connection = new ProtocolConnection(pipe);

        var openRequestId = Guid.NewGuid();
        await connection.WriteAsync(
            WireEnvelope.Request(
                ProtocolOperations.ConnectionOpen,
                openRequestId,
                new ConnectionOpenRequest(
                    _manifest.WorkerInstanceId,
                    _arguments.LaunchToken,
                    _manifest.RuntimeProfileDigest)),
            cancellationToken);
        var openResponse = await connection.ReadAsync(cancellationToken)
                           ?? throw new EndOfStreamException("connection.open 前 Pipe 已关闭。 ");
        ValidateResponse(openResponse, ProtocolOperations.ConnectionOpen, openRequestId);
        if (openResponse.Success != true) {
            throw new UnauthorizedAccessException(
                $"Worker admission 被拒绝：{openResponse.Error?.Code} - {openResponse.Error?.Message}");
        }
        Log("INFO", "ipc.lifecycle", "Worker admission 成功。 ");

        await ServeConnectionAsync(connection, cancellationToken);
    }

    internal async Task ServeConnectionAsync(ProtocolConnection connection, CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid();
        _connectionId = connectionId;
        await using var events = new WorkerEventSender(connection);
        lock (_eventsGate) {
            _events = events;
        }
        try {
            while (!cancellationToken.IsCancellationRequested) {
                var envelope = await connection.ReadAsync(cancellationToken);
                if (envelope is null) {
                    return;
                }
                var response = HandleRequest(envelope, out var pendingStop);
                await connection.WriteAsync(response, cancellationToken);
                if (pendingStop is not null) {
                    await BeginStopAsync(pendingStop, connection, cancellationToken);
                }
            }
        } finally {
            _preview.Disconnect(connectionId);
            lock (_eventsGate) {
                if (ReferenceEquals(_events, events)) {
                    _events = null;
                }
            }
        }
    }

    private WireEnvelope HandleRequest(WireEnvelope request, out RunSupervisor.PendingStop? pendingStop)
    {
        pendingStop = null;
        if (request.MessageType != ProtocolMessageTypes.Request || request.RequestId is not Guid requestId) {
            throw new ProtocolException("Worker 只接受带 requestId 的 request envelope。 ");
        }
        if (request.ProtocolVersion != ProtocolConstants.ProtocolVersion) {
            return WireEnvelope.Failure(
                request.Operation, requestId, "protocol_version_mismatch",
                $"Worker protocol={ProtocolConstants.ProtocolVersion}，request={request.ProtocolVersion}。 ");
        }

        try {
            return request.Operation switch {
                ProtocolOperations.WorkerGetSnapshot => WireEnvelope.Response(
                    request.Operation, requestId, new GetSnapshotResponse(GetSnapshot())),
                ProtocolOperations.RunStart => WireEnvelope.Response(
                    request.Operation, requestId,
                    _supervisor.Accept(ProtocolJson.Deserialize<RunStartRequest>(request.Data))),
                ProtocolOperations.RunStop => WireEnvelope.Response(
                    request.Operation, requestId,
                    _supervisor.Stop(ProtocolJson.Deserialize<RunStopRequest>(request.Data), out pendingStop)),
                ProtocolOperations.LogGetSince => WireEnvelope.Response(
                    request.Operation, requestId,
                    GetLogs(ProtocolJson.Deserialize<LogGetSinceRequest>(request.Data))),
                ProtocolOperations.PreviewStart or ProtocolOperations.PreviewRenew
                    or ProtocolOperations.PreviewStop => HandlePreview(request.Operation, requestId,
                        ProtocolJson.Deserialize<PreviewRequest>(request.Data)),
                _ => throw new WorkerRequestException("invalid_request", $"未知 operation：{request.Operation}。 ")
            };
        } catch (WorkerRequestException exception) {
            return WireEnvelope.Failure(request.Operation, requestId, exception.Code, exception.Message);
        } catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException) {
            return WireEnvelope.Failure(
                request.Operation, requestId, "invalid_request",
                exception.GetBaseException().Message);
        } catch (Exception exception) {
            Log("ERROR", "ipc.request", $"处理 {request.Operation} 失败：{exception}");
            return WireEnvelope.Failure(
                request.Operation, requestId, "internal_error",
                exception.GetBaseException().Message);
        }
    }

    private async Task BeginStopAsync(
        RunSupervisor.PendingStop pendingStop, ProtocolConnection connection, CancellationToken cancellationToken)
    {
        // The stop ACK has already been written on this connection. Send the complete Stopping
        // snapshot before MaaFramework Stop can finish so the acceptance state is observable.
        try {
            await connection.WriteAsync(
                WireEnvelope.Event(
                    ProtocolOperations.RunStateChanged, new StateChangedEvent(
                        _manifest.WorkerInstanceId,
                        pendingStop.StoppingSnapshot.StateRevision, pendingStop.StoppingSnapshot)),
                cancellationToken);
        } finally {
            pendingStop.Begin();
        }
    }

    private WorkerRuntimeExecution CreateRuntimeExecution(Guid runId, RunPlanItem item, Action onRunning) => new(
        _manifest, runId, item, checked((uint)Process.GetCurrentProcess().SessionId),
        (level, source, message) => Log(level, source, message, runId, item.PlanItemId, item.TaskName), onRunning);

    private LogGetSinceResponse GetLogs(LogGetSinceRequest request)
    {
        try {
            return _logs.GetSince(request.AfterSequence, request.Limit);
        } catch (ArgumentOutOfRangeException) {
            throw new WorkerRequestException("invalid_request", "afterSequence 必须 >=0 且 limit 必须 >0。 ");
        }
    }

    private WireEnvelope HandlePreview(string operation, Guid requestId, PreviewRequest request)
    {
        if (operation != ProtocolOperations.PreviewStop && !_supervisor.IsReady) {
            throw new WorkerRequestException("worker_not_ready", "Worker 尚未就绪。");
        }
        return WireEnvelope.Response(operation, requestId, _preview.Handle(operation, _connectionId, request));
    }

    public void Dispose() => _preview.Dispose();

    internal WorkerSnapshot GetSnapshot() => _supervisor.GetSnapshot();

    private void PublishState(string operation, WorkerSnapshot snapshot)
    {
        WorkerEventSender? events;
        lock (_eventsGate) {
            events = _events;
        }
        events?.PublishState(WireEnvelope.Event(
            operation, new StateChangedEvent(_manifest.WorkerInstanceId, snapshot.StateRevision, snapshot)));
    }

    private void Log(
        string level, string source, string message,
        Guid? runId = null, Guid? planItemId = null, string? taskName = null)
    {
        var entry = _logs.Add(level, source, message, runId, planItemId, taskName);
        Console.WriteLine($"[{entry.TimestampUtc:O}] [{level}] [{source}] {entry.Message}");
        WorkerEventSender? events;
        lock (_eventsGate) {
            events = _events;
        }
        events?.PublishLog(WireEnvelope.Event(
            ProtocolOperations.LogEntry, new LogEntryEvent(_manifest.WorkerInstanceId, entry)));
    }

    private static void ValidateResponse(WireEnvelope response, string operation, Guid requestId)
    {
        if (response.MessageType != ProtocolMessageTypes.Response
            || response.Operation != operation
            || response.RequestId != requestId) {
            throw new ProtocolException("connection.open response 与请求不匹配。 ");
        }
    }
}

internal sealed class WorkerRequestException : Exception
{
    internal WorkerRequestException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
