using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed class WorkerHost
{
    private sealed record DeferredStop(Guid RunId, WorkerRuntimeExecution Execution, WorkerSnapshot StoppingSnapshot);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private readonly object _stateGate = new();
    private readonly WorkerArguments _arguments;
    private readonly LaunchManifest _manifest;
    private readonly WorkerLogBuffer _logs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, (string Digest, RunSnapshot? Terminal)> _ledger = new();
    private WorkerEventSender? _events;
    private WorkerState _workerState = WorkerState.Starting;
    private StructuredReason? _workerReason;
    private DependencyStatus _dependencyStatus;
    private RunState _runState = RunState.Idle;
    private RunSnapshot? _activeRun;
    private RunSnapshot? _lastRun;
    private WorkerRuntimeExecution? _execution;
    private long _stateRevision = 1;

    internal WorkerHost(WorkerArguments arguments, LaunchManifest manifest)
    {
        _arguments = arguments;
        _manifest = manifest;
        var unavailable = new DependencyCheck(false, null, "尚未检查");
        _dependencyStatus = new DependencyStatus(
            DateTime.UtcNow,
            typeof(WorkerHost).Assembly.GetName().Version?.ToString() ?? "unknown",
            "starting",
            unavailable,
            unavailable,
            unavailable,
            unavailable,
            unavailable);
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

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var (status, reason) = await DependencyProbe.RunAsync(_manifest, cancellationToken);
        WorkerSnapshot snapshot;
        lock (_stateGate) {
            _dependencyStatus = status;
            _workerReason = reason;
            _workerState = reason is null ? WorkerState.Ready : WorkerState.NotReady;
            snapshot = CommitLocked();
        }
        PublishState(ProtocolOperations.WorkerStateChanged, snapshot);
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
                    GetWorkerVersion(),
                    _manifest.RuntimeProfileDigest)),
            cancellationToken);
        var openResponse = await connection.ReadAsync(cancellationToken)
                           ?? throw new EndOfStreamException("connection.open 前 Pipe 已关闭。 ");
        ValidateResponse(openResponse, ProtocolOperations.ConnectionOpen, openRequestId);
        if (openResponse.Success != true) {
            throw new UnauthorizedAccessException(
                $"Worker admission 被拒绝：{openResponse.Error?.Code} - {openResponse.Error?.Message}");
        }
        _ = ProtocolJson.Deserialize<ConnectionOpenResponse>(openResponse.Data);
        Log("INFO", "ipc.lifecycle", "Worker admission 成功。 ");

        await using var events = new WorkerEventSender(connection);
        lock (_stateGate) {
            _events = events;
        }
        try {
            while (!cancellationToken.IsCancellationRequested) {
                var envelope = await connection.ReadAsync(cancellationToken);
                if (envelope is null) {
                    return;
                }
                var response = HandleRequest(envelope, out var deferredStop);
                await connection.WriteAsync(response, cancellationToken);
                if (deferredStop is not null) {
                    await BeginDeferredStopAsync(deferredStop, connection, cancellationToken);
                }
            }
        } finally {
            lock (_stateGate) {
                if (ReferenceEquals(_events, events)) {
                    _events = null;
                }
            }
        }
    }

    private WireEnvelope HandleRequest(WireEnvelope request, out DeferredStop? deferredStop)
    {
        deferredStop = null;
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
                    AcceptRun(ProtocolJson.Deserialize<RunStartRequest>(request.Data))),
                ProtocolOperations.RunStop => WireEnvelope.Response(
                    request.Operation, requestId,
                    AcceptStop(ProtocolJson.Deserialize<RunStopRequest>(request.Data), out deferredStop)),
                ProtocolOperations.LogGetSince => WireEnvelope.Response(
                    request.Operation, requestId,
                    GetLogs(ProtocolJson.Deserialize<LogGetSinceRequest>(request.Data))),
                ProtocolOperations.PreviewGetLatest => HandlePreviewGetLatest(
                    request.Operation, requestId,
                    ProtocolJson.Deserialize<PreviewGetLatestRequest>(request.Data)),
                _ => throw new WorkerRequestException("invalid_request", $"未知 operation：{request.Operation}。 ")
            };
        } catch (WorkerRequestException exception) {
            return WireEnvelope.Failure(
                request.Operation, requestId, exception.Code, exception.Message, exception.Retriable);
        } catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException) {
            return WireEnvelope.Failure(
                request.Operation, requestId, "invalid_request",
                exception.GetBaseException().Message);
        } catch (Exception exception) {
            Log("ERROR", "ipc.request", $"处理 {request.Operation} 失败：{exception}");
            return WireEnvelope.Failure(
                request.Operation, requestId, "internal_error",
                exception.GetBaseException().Message, retriable: false);
        }
    }

    private RunStartResponse AcceptRun(RunStartRequest request)
    {
        WorkerRuntimeExecution execution;
        RunSnapshot run;
        WorkerSnapshot snapshot;
        lock (_stateGate) {
            if (_ledger.TryGetValue(request.RunId, out var existing)) {
                if (existing.Digest != request.PlanDigest) {
                    throw new WorkerRequestException("run_id_conflict", "相同 runId 使用了不同 planDigest。 ");
                }
                var existingRun = _activeRun?.RunId == request.RunId
                    ? _activeRun
                    : _lastRun?.RunId == request.RunId
                        ? _lastRun
                        : existing.Terminal;
                if (existingRun is not null) {
                    return new RunStartResponse("already_accepted", existingRun);
                }
            }
            if (_workerState == WorkerState.NotReady) {
                throw new WorkerRequestException("worker_not_ready", _workerReason?.Message ?? "Worker NotReady。 ");
            }
            if (_workerState == WorkerState.Faulted) {
                throw new WorkerRequestException("worker_faulted", _workerReason?.Message ?? "Worker Faulted。 ");
            }
            if (_workerState != WorkerState.Ready) {
                throw new WorkerRequestException("operation_not_allowed", $"Worker state={_workerState}。 ");
            }
            if (_activeRun is not null) {
                throw new WorkerRequestException("worker_busy", "Worker 已有 active Run。 ", retriable: true);
            }
            ValidateRunPlan(request);

            var item = request.Plan.Items[0];
            var startedAtUtc = DateTime.UtcNow;
            var itemSnapshots = request.Plan.Items.Select((candidate, index) => new PlanItemSnapshot(
                candidate.PlanItemId, candidate.TaskName, candidate.TaskLabel, candidate.Entry,
                candidate.ResolvedOptions, candidate.PipelineOverride,
                index == 0 ? PlanItemState.Starting : PlanItemState.Pending,
                index == 0 ? startedAtUtc : null, null, null, null, null)).ToArray();
            run = new RunSnapshot(
                request.RunId, request.PlanDigest, RunState.Starting, request.Plan.CreatedAtUtc,
                startedAtUtc, null, null, item.PlanItemId, 0,
                request.Plan, itemSnapshots, null, null);
            _lastRun = null;
            _activeRun = run;
            _runState = RunState.Starting;
            _ledger.Add(request.RunId, (request.PlanDigest, null));
            execution = CreateExecution(request.RunId, item);
            _execution = execution;
            snapshot = CommitLocked();
        }

        PublishState(ProtocolOperations.RunStateChanged, snapshot);
        Log(
            "INFO", "run.lifecycle",
            $"Run 已接受：{request.RunId}，items={run.Items.Count}，first={run.Items[0].TaskName}。 ", request.RunId);
        _ = Task.Run(() => ExecuteRunAsync(request.RunId, execution, _shutdown.Token));
        return new RunStartResponse("accepted", run);
    }

    private RunStopResponse AcceptStop(RunStopRequest request, out DeferredStop? deferredStop)
    {
        deferredStop = null;
        WorkerRuntimeExecution execution;
        WorkerSnapshot snapshot;
        lock (_stateGate) {
            if (_activeRun is null) {
                if (_lastRun?.RunId == request.RunId) {
                    return new RunStopResponse("already_terminal", _lastRun.State);
                }
                throw new WorkerRequestException("run_id_mismatch", "没有匹配的 active Run。 ");
            }
            if (_activeRun.RunId != request.RunId) {
                throw new WorkerRequestException("run_id_mismatch", "runId 与 active Run 不一致。 ");
            }
            if (_activeRun.State == RunState.Stopping) {
                return new RunStopResponse("already_stopping", RunState.Stopping);
            }
            if (_activeRun.State is not (RunState.Starting or RunState.Running)) {
                throw new WorkerRequestException("operation_not_allowed", $"Run state={_activeRun.State}。 ");
            }

            var now = DateTime.UtcNow;
            var items = _activeRun.Items.Select(item =>
                item.State == PlanItemState.Pending
                    ? item with {
                        State = PlanItemState.Cancelled,
                        EndedAtUtc = now,
                        Reason = "user_requested"
                    }
                    : item).ToArray();
            _activeRun = _activeRun with {
                State = RunState.Stopping,
                StopRequestedAtUtc = now,
                Items = items
            };
            _runState = RunState.Stopping;
            execution = _execution
                        ?? throw new WorkerRequestException("internal_error", "active Run 缺少 execution context。 ");
            snapshot = CommitLocked();
        }

        execution.RequestStop();
        deferredStop = new DeferredStop(request.RunId, execution, snapshot);
        return new RunStopResponse("stop_requested", RunState.Stopping);
    }

    private async Task BeginDeferredStopAsync(
        DeferredStop deferred, ProtocolConnection connection, CancellationToken cancellationToken)
    {
        // The stop ACK has already been written on this connection. Send the complete Stopping
        // snapshot before MaaFramework Stop can finish so the acceptance state is observable.
        try {
            await connection.WriteAsync(
                WireEnvelope.Event(
                    ProtocolOperations.RunStateChanged, new StateChangedEvent(
                        _manifest.WorkerInstanceId,
                        deferred.StoppingSnapshot.StateRevision, deferred.StoppingSnapshot)),
                cancellationToken);
        } finally {
            Log("INFO", "run.stop", $"已接受 run.stop：{deferred.RunId}。 ", deferred.RunId);
            _ = Task.Run(async () =>
            {
                try {
                    await deferred.Execution.StopAsync(_shutdown.Token);
                } catch (Exception exception) {
                    Log(
                        "ERROR", "run.stop",
                        $"MaaFramework Stop 确认失败：{exception.GetBaseException().Message}",
                        deferred.RunId);
                }
            });
        }
    }

    private async Task ExecuteRunAsync(
        Guid runId, WorkerRuntimeExecution execution, CancellationToken cancellationToken)
    {
        while (true) {
            var result = await execution.ExecuteAsync(cancellationToken);
            WorkerRuntimeExecution? nextExecution = null;
            WorkerSnapshot snapshot;
            lock (_stateGate) {
                if (_activeRun?.RunId != runId || !ReferenceEquals(_execution, execution)) {
                    return;
                }

                var now = DateTime.UtcNow;
                if (result.Outcome == RuntimeExecutionOutcome.StopTimedOut) {
                    _workerState = WorkerState.Faulted;
                    _workerReason = result.Error;
                    _runState = RunState.Stopping;
                    snapshot = CommitLocked();
                } else {
                    var currentIndex = _activeRun.CurrentPlanItemIndex
                                       ?? throw new InvalidOperationException("active Run 缺少 current item index。 ");
                    var wasStopping = _activeRun.State == RunState.Stopping;
                    if (result.Outcome == RuntimeExecutionOutcome.Succeeded
                        && !wasStopping && currentIndex + 1 < _activeRun.Items.Count) {
                        var items = _activeRun.Items.ToArray();
                        items[currentIndex] = items[currentIndex] with {
                            State = PlanItemState.Succeeded,
                            EndedAtUtc = now,
                            Result = result.Result
                        };
                        var nextIndex = currentIndex + 1;
                        items[nextIndex] = items[nextIndex] with {
                            State = PlanItemState.Starting,
                            StartedAtUtc = now
                        };
                        var nextItem = _activeRun.Plan.Items[nextIndex];
                        _activeRun = _activeRun with {
                            State = RunState.Running,
                            CurrentPlanItemId = nextItem.PlanItemId,
                            CurrentPlanItemIndex = nextIndex,
                            Items = items
                        };
                        _runState = RunState.Running;
                        nextExecution = CreateExecution(runId, nextItem);
                        _execution = nextExecution;
                        snapshot = CommitLocked();
                    } else {
                        snapshot = CompleteRunLocked(runId, currentIndex, result, wasStopping, now);
                    }
                }
            }

            PublishState(ProtocolOperations.RunStateChanged, snapshot);
            if (nextExecution is null) {
                Log(
                    result.Outcome is RuntimeExecutionOutcome.Succeeded or RuntimeExecutionOutcome.Cancelled
                        ? "INFO"
                        : "ERROR",
                    "run.lifecycle", $"Run 终结：{runId}，outcome={result.Outcome}。 ", runId);
                return;
            }

            Log("INFO", "run.lifecycle", $"开始执行下一 Plan Item：{runId}。 ", runId);
            execution = nextExecution;
        }
    }

    private WorkerSnapshot CompleteRunLocked(
        Guid runId, int currentIndex, RuntimeExecutionResult result, bool wasStopping, DateTime now)
    {
        var finalRunState = ResolveFinalRunState(result.Outcome, wasStopping);
        var finalItemState = finalRunState switch {
            RunState.Succeeded => PlanItemState.Succeeded,
            RunState.Cancelled => PlanItemState.Cancelled,
            _ => PlanItemState.Failed
        };
        var items = _activeRun!.Items.ToArray();
        items[currentIndex] = items[currentIndex] with {
            State = finalItemState,
            EndedAtUtc = now,
            Reason = finalRunState == RunState.Cancelled ? "user_requested" : null,
            Result = result.Result,
            Error = finalRunState == RunState.Failed ? result.Error : null
        };
        var pendingReason = finalRunState == RunState.Cancelled ? "user_requested" : "prior_item_failed";
        for (var index = currentIndex + 1; index < items.Length; index++) {
            if (items[index].State == PlanItemState.Pending) {
                items[index] = items[index] with {
                    State = PlanItemState.Cancelled,
                    EndedAtUtc = now,
                    Reason = pendingReason
                };
            }
        }

        var terminal = _activeRun with {
            State = finalRunState,
            EndedAtUtc = now,
            CurrentPlanItemId = null,
            CurrentPlanItemIndex = null,
            Items = items,
            Result = result.Result,
            Error = finalRunState == RunState.Failed ? result.Error : null
        };
        _activeRun = null;
        _lastRun = terminal;
        _runState = RunState.Idle;
        _execution = null;
        _ledger[runId] = (_ledger[runId].Digest, terminal);
        if (result.Outcome == RuntimeExecutionOutcome.CleanupFailed) {
            _workerState = WorkerState.Faulted;
            _workerReason = result.Error;
        } else {
            _workerState = WorkerState.Ready;
            _workerReason = null;
        }
        return CommitLocked();
    }

    internal static RunState ResolveFinalRunState(RuntimeExecutionOutcome outcome, bool wasStopping)
    {
        if (wasStopping) {
            return RunState.Cancelled;
        }
        return outcome switch {
            RuntimeExecutionOutcome.Succeeded => RunState.Succeeded,
            RuntimeExecutionOutcome.Cancelled => RunState.Cancelled,
            _ => RunState.Failed
        };
    }

    private WorkerRuntimeExecution CreateExecution(Guid runId, RunPlanItem item) => new(
        _manifest, runId, item, checked((uint)Process.GetCurrentProcess().SessionId),
        (level, source, message) => Log(level, source, message, runId, item.PlanItemId, item.TaskName),
        () => MarkRunRunning(runId, item.PlanItemId));

    private void MarkRunRunning(Guid runId, Guid planItemId)
    {
        WorkerSnapshot snapshot;
        lock (_stateGate) {
            if (_activeRun?.RunId != runId || _activeRun.CurrentPlanItemId != planItemId
                || _activeRun.State == RunState.Stopping) {
                return;
            }
            var currentIndex = _activeRun.CurrentPlanItemIndex
                               ?? throw new InvalidOperationException("active Run 缺少 current item index。 ");
            var items = _activeRun.Items.ToArray();
            if (items[currentIndex].State != PlanItemState.Starting) {
                return;
            }
            items[currentIndex] = items[currentIndex] with { State = PlanItemState.Running };
            _activeRun = _activeRun with { State = RunState.Running, Items = items };
            _runState = RunState.Running;
            snapshot = CommitLocked();
        }
        PublishState(ProtocolOperations.RunStateChanged, snapshot);
    }

    private LogGetSinceResponse GetLogs(LogGetSinceRequest request)
    {
        try {
            return _logs.GetSince(request.AfterSequence, request.Limit);
        } catch (ArgumentOutOfRangeException) {
            throw new WorkerRequestException("invalid_request", "afterSequence 必须 >=0 且 limit 必须 >0。 ");
        }
    }

    private WireEnvelope HandlePreviewGetLatest(string operation, Guid requestId, PreviewGetLatestRequest request)
    {
        if (request.AfterRevision < 0) {
            throw new WorkerRequestException("invalid_request", "afterRevision 必须 >=0。 ");
        }

        WorkerRuntimeExecution? execution;
        Guid? activeRunId;
        lock (_stateGate) {
            execution = _execution;
            activeRunId = _activeRun?.RunId;
        }

        PreviewGetLatestResponse response;
        if (execution is null || activeRunId is null) {
            response = PreviewUnavailable(activeRunId, "no_active_run");
        } else if (activeRunId != request.RunId) {
            response = PreviewUnavailable(activeRunId, "run_mismatch");
        } else if (execution.ReadLatestPreview() is not { } frame) {
            response = PreviewUnavailable(activeRunId, "no_frame");
        } else if (frame.Revision > request.AfterRevision) {
            response = new PreviewGetLatestResponse(
                "frame", _manifest.WorkerInstanceId, frame.RunId, frame.Revision,
                frame.SampledAtUtc, frame.PixelWidth, frame.PixelHeight, "image/png",
                frame.PngBytes, null);
        } else {
            response = new PreviewGetLatestResponse(
                "not_modified", _manifest.WorkerInstanceId, activeRunId, frame.Revision,
                null, null, null, null, null, null);
        }

        var envelope = WireEnvelope.Response(operation, requestId, response);
        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options).Length;
        if (responseBytes <= ProtocolConstants.MaximumPreviewResponseBytes) {
            return envelope;
        }

        Log(
            "WARN", "preview.transport",
            $"Preview response 超过预算：{responseBytes} > {ProtocolConstants.MaximumPreviewResponseBytes} bytes。 ",
            activeRunId);
        return WireEnvelope.Response(operation, requestId, PreviewUnavailable(activeRunId, "frame_too_large"));
    }

    private PreviewGetLatestResponse PreviewUnavailable(Guid? runId, string reason) => new(
        "unavailable", _manifest.WorkerInstanceId, runId, 0,
        null, null, null, null, null, reason);

    private void ValidateRunPlan(RunStartRequest request)
    {
        CanonicalDigest.ValidateDigestFormat(request.PlanDigest, nameof(request.PlanDigest));
        if (request.Plan.PlanVersion != ProtocolConstants.PlanVersion) {
            throw new WorkerRequestException("invalid_run_plan", "不支持 planVersion。 ");
        }
        if (request.Plan.Items.Count == 0) {
            throw new WorkerRequestException("invalid_run_plan", "Run Plan 必须至少包含一个 Plan Item。 ");
        }
        if (request.Plan.Items.Select(item => item.PlanItemId).Distinct().Count() != request.Plan.Items.Count) {
            throw new WorkerRequestException("invalid_run_plan", "Plan Item ID 不唯一。 ");
        }
        if (request.Plan.RuntimeProfileDigest != _manifest.RuntimeProfileDigest) {
            throw new WorkerRequestException("worker_not_ready", "Run Plan Runtime Profile Digest 与 Worker 不一致。 ");
        }
        var actualDigest = CanonicalDigest.ComputePlanDigestV1(request.Plan);
        if (actualDigest != request.PlanDigest) {
            throw new WorkerRequestException("invalid_run_plan", "planDigest 重算不一致。 ");
        }
        var planBytes = JsonSerializer.SerializeToUtf8Bytes(request.Plan, ProtocolJson.Options).Length;
        if (planBytes > ProtocolConstants.MaximumRunPlanBytes) {
            throw new WorkerRequestException(
                "invalid_run_plan",
                $"Run Plan 超过 {ProtocolConstants.MaximumRunPlanBytes} bytes。 ");
        }

        var candidate = GetSnapshotLocked() with {
            ActiveRun = new RunSnapshot(
                request.RunId, request.PlanDigest, RunState.Starting, request.Plan.CreatedAtUtc,
                DateTime.UtcNow, null, null, request.Plan.Items[0].PlanItemId, 0,
                request.Plan, [], null, null),
            RunState = RunState.Starting
        };
        var candidateBytes = JsonSerializer.SerializeToUtf8Bytes(
            WireEnvelope.Response(
                ProtocolOperations.WorkerGetSnapshot, Guid.Empty, new GetSnapshotResponse(candidate)),
            ProtocolJson.Options).Length;
        if (candidateBytes + 512 * 1024 > ProtocolConstants.MaximumSnapshotPayloadBytes) {
            throw new WorkerRequestException(
                "invalid_run_plan",
                $"Run 接受后无法满足 Snapshot terminal reserve：base={candidateBytes}。 ");
        }
    }

    private WorkerSnapshot GetSnapshot()
    {
        lock (_stateGate) {
            return GetSnapshotLocked();
        }
    }

    private WorkerSnapshot CommitLocked()
    {
        _stateRevision++;
        return GetSnapshotLocked();
    }

    private WorkerSnapshot GetSnapshotLocked()
    {
        var (firstLog, lastLog) = _logs.GetRange();
        return new WorkerSnapshot(
            ProtocolConstants.SnapshotVersion, DateTime.UtcNow, _stateRevision, _manifest.WorkerInstanceId,
            Environment.ProcessId, checked((uint)Process.GetCurrentProcess().SessionId), GetWorkerVersion(),
            ProtocolConstants.ProtocolVersion, _manifest.RuntimeProfileDigest, _manifest.Project,
            _workerState, _workerReason, _dependencyStatus, _runState,
            _activeRun, _lastRun, firstLog, lastLog);
    }

    private void PublishState(string operation, WorkerSnapshot snapshot)
    {
        WorkerEventSender? events;
        lock (_stateGate) {
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
        lock (_stateGate) {
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

    private static string GetWorkerVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}

internal sealed class WorkerRequestException : Exception
{
    internal WorkerRequestException(string code, string message, bool retriable = false)
        : base(message)
    {
        Code = code;
        Retriable = retriable;
    }

    internal string Code { get; }
    internal bool Retriable { get; }
}
