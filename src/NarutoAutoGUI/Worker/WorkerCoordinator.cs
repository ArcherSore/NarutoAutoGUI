using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal enum WorkerObservation
{
    WorkerNotStarted,
    WorkerStarting,
    Connected,
    IpcDisconnected,
    WorkerExited,
    WorkerRecoveryConflict,
    ChildSessionEnded
}

internal sealed record WorkerCoordinatorSnapshot(
    WorkerObservation Observation, bool SnapshotFresh,
    WorkerSnapshot? WorkerSnapshot, string Detail)
{
    internal static WorkerCoordinatorSnapshot Empty { get; } = new(
        WorkerObservation.WorkerNotStarted, false, null, "Worker 尚未启动");
}

internal sealed class WorkerCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LogRecoveryRetryDelay = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly object _logDispatchGate = new();
    private readonly AppLogger _logger;
    private readonly WorkerAdmissionStore _store;
    private readonly ChildSessionWorkerLauncher _launcher;
    private readonly string _workerExecutablePath;
    private readonly string _pipeName;
    private readonly bool _usePipeAcl;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<WireEnvelope>> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte> _abandonedRequests = new();
    private readonly TaskCompletionSource<bool> _serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serverTask;
    private ProtocolConnection? _connection;
    private WorkerAdmissionRecord? _admission;
    private TaskCompletionSource<WorkerSnapshot>? _awaitedFreshSnapshot;
    private readonly WorkerLogSequenceTracker _logSequence = new();
    private Task? _logRecoveryTask;
    private int _logRecoveryGeneration;

    internal WorkerCoordinator(AppLogger logger, string stateDirectory, string workerExecutablePath)
        : this(logger, stateDirectory, workerExecutablePath, PipeIdentity.ForCurrentUser(), usePipeAcl: true)
    {
    }

    internal WorkerCoordinator(
        AppLogger logger, string stateDirectory, string workerExecutablePath,
        string pipeName, bool usePipeAcl)
    {
        _logger = logger;
        _store = new WorkerAdmissionStore(stateDirectory);
        _launcher = new ChildSessionWorkerLauncher(logger);
        _workerExecutablePath = Path.GetFullPath(workerExecutablePath);
        _pipeName = pipeName;
        _usePipeAcl = usePipeAcl;
        try {
            _admission = _store.Load();
            Snapshot = _admission is null
                ? WorkerCoordinatorSnapshot.Empty
                : new WorkerCoordinatorSnapshot(
                    _admission.WorkerPid is null
                        ? WorkerObservation.WorkerStarting
                        : WorkerObservation.IpcDisconnected,
                    false, null,
                    "已加载 Worker Admission Record，等待 Worker 连接");
        } catch (Exception exception) {
            Snapshot = new WorkerCoordinatorSnapshot(
                WorkerObservation.WorkerRecoveryConflict, false, null,
                exception.GetBaseException().Message);
            _logger.Error("读取 Worker Admission Record 失败。", exception);
        }
        _serverTask = Task.Run(() => ServerLoopAsync(_shutdown.Token));
        _ = _serverTask.ContinueWith(
            task => _serverReady.TrySetException(task.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal event EventHandler<WorkerCoordinatorSnapshot>? StateChanged;
    internal event EventHandler<WorkerLogEntry>? LogReceived;

    internal WorkerCoordinatorSnapshot Snapshot { get; private set; }

    internal Task WaitForServerReadyAsync(CancellationToken cancellationToken) =>
        _serverReady.Task.WaitAsync(cancellationToken);

    internal async Task<WorkerSnapshot> PrepareWorkerAsync(uint childSessionId, ProjectPlanModule project, CancellationToken cancellationToken = default)
    {
        await WaitForServerReadyAsync(cancellationToken);
        lock (_gate) {
            if (Snapshot.Observation == WorkerObservation.Connected && Snapshot.SnapshotFresh
                && Snapshot.WorkerSnapshot?.RuntimeProfileDigest == project.RuntimeProfileDigest) {
                return Snapshot.WorkerSnapshot;
            }
            if (_admission is not null) {
                throw new InvalidOperationException(
                    "已有 Worker Admission Record；首片尚未提供 Runtime Profile replacement UI。 ");
            }
        }

        var instanceId = Guid.NewGuid();
        var launchToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var manifest = project.CreateLaunchManifest(instanceId);
        var manifestPath = _store.GetManifestPath(instanceId);
        var admission = new WorkerAdmissionRecord(
            instanceId, launchToken, childSessionId, null,
            project.RuntimeProfileDigest, DateTime.UtcNow);
        try {
            _store.SaveManifest(manifest);
            _store.SaveRecord(admission);
        } catch {
            _store.DeleteManifest(instanceId);
            throw;
        }

        TaskCompletionSource<WorkerSnapshot> fresh;
        lock (_gate) {
            _admission = admission;
            fresh = new TaskCompletionSource<WorkerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitedFreshSnapshot = fresh;
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.WorkerStarting,
                false,
                null,
                "Pending Admission 已写入，正在启动 Worker"));
        }
        RaiseStateChanged();

        try {
            var launch = await _launcher.LaunchAsync(
                childSessionId, _workerExecutablePath, instanceId,
                launchToken, manifestPath, cancellationToken);
            RecordVerifiedWorkerProcess(instanceId, launch);
        } catch (Exception exception) {
            var observedThroughPipe = false;
            lock (_gate) {
                observedThroughPipe = _admission is {
                    WorkerInstanceId: var currentInstance,
                    WorkerPid: not null
                } current
                                      && currentInstance == instanceId
                                      && IsExpectedWorkerAlive(current);
                if (!observedThroughPipe) {
                    _admission = null;
                    _awaitedFreshSnapshot = null;
                    UpdateSnapshotLocked(WorkerCoordinatorSnapshot.Empty);
                }
            }
            if (observedThroughPipe) {
                _logger.Warn("Task Scheduler 进程枚举验证失败，但同一 Worker 已通过 Pipe PID/Session/映像校验；继续等待 fresh Snapshot。", exception);
            } else {
                _store.DeleteRecord();
                _store.DeleteManifest(instanceId);
                RaiseStateChanged();
                throw;
            }
        }

        try {
            var snapshot = await fresh.Task.WaitAsync(AdmissionTimeout, cancellationToken);
            _store.DeleteManifest(instanceId);
            return snapshot;
        } catch (TimeoutException exception) {
            WorkerAdmissionRecord? timedOutAdmission = null;
            WorkerSnapshot? lateSnapshot = null;
            var rolledBack = false;
            lock (_gate) {
                if (Snapshot is {
                    Observation: WorkerObservation.Connected,
                    SnapshotFresh: true,
                    WorkerSnapshot: { WorkerInstanceId: var snapshotInstance } workerSnapshot
                }
                    && snapshotInstance == instanceId) {
                    lateSnapshot = workerSnapshot;
                } else if (_admission?.WorkerInstanceId == instanceId) {
                    timedOutAdmission = _admission;
                    rolledBack = timedOutAdmission.WorkerPid is null
                                 || !IsExpectedWorkerAlive(timedOutAdmission);
                    _awaitedFreshSnapshot = null;
                    if (rolledBack) {
                        _admission = null;
                        UpdateSnapshotLocked(WorkerCoordinatorSnapshot.Empty with {
                            Detail = "Worker admission 超时且没有存活的已验证进程；Pending Admission 已回滚"
                        });
                    } else {
                        UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                            WorkerObservation.IpcDisconnected, false, Snapshot.WorkerSnapshot,
                            $"Worker PID {timedOutAdmission.WorkerPid} 仍存活，但未按时完成 admission + fresh Snapshot"));
                    }
                }
            }

            if (lateSnapshot is not null) {
                _store.DeleteManifest(instanceId);
                _logger.Info("Worker fresh Snapshot 在 admission 超时边界完成；按成功处理。 ");
                return lateSnapshot;
            }

            if (rolledBack) {
                _store.DeleteRecord();
                _store.DeleteManifest(instanceId);
                _logger.Warn(
                    $"Worker admission 在 {AdmissionTimeout.TotalSeconds:0} 秒后超时；"
                    + "未发现存活的已验证 Worker，已回滚 worker.json 与 launch manifest。 ");
            } else if (timedOutAdmission is not null) {
                _logger.Warn(
                    $"Worker admission 在 {AdmissionTimeout.TotalSeconds:0} 秒后超时；"
                    + $"PID={timedOutAdmission.WorkerPid}、SessionId={timedOutAdmission.ChildSessionId} 仍存活，"
                    + "保留 Admission 供 Worker 重连。 ");
            }
            RaiseStateChanged();
            var disposition = rolledBack
                ? "未发现存活的已验证 Worker，Pending Admission 已自动回滚。 "
                : timedOutAdmission is not null
                    ? "已验证 Worker 仍存活，Admission 已保留供重连。 "
                    : "Admission 已由 Child Session 生命周期清理。 ";
            throw new TimeoutException(
                $"Worker 未在 {AdmissionTimeout.TotalSeconds:0} 秒内完成 admission + fresh Snapshot；"
                + disposition,
                exception);
        }
    }

    private void RecordVerifiedWorkerProcess(Guid workerInstanceId, VerifiedChildSessionProcessLaunch launch)
    {
        var workerPid = checked((int)launch.ProcessId);
        WorkerAdmissionRecord? recordToPersist = null;
        var changed = false;
        lock (_gate) {
            var current = _admission
                          ?? throw new InvalidOperationException("Worker 进程启动后 Admission Record 已不存在。 ");
            if (current.WorkerInstanceId != workerInstanceId) {
                throw new InvalidOperationException("Worker 进程启动后 Admission instance 已发生变化。 ");
            }
            if (current.ChildSessionId != launch.SessionId) {
                throw new InvalidOperationException("Worker 启动验证返回了错误的 Child Session。 ");
            }
            if (current.WorkerPid is int admittedPid && admittedPid != workerPid) {
                throw new InvalidOperationException(
                    $"Task Scheduler 验证 PID={workerPid}，但 Pipe Admission PID={admittedPid}。 ");
            }
            if (current.WorkerPid is null) {
                current = current with { WorkerPid = workerPid };
                _admission = current;
                recordToPersist = current;
                changed = true;
            }
            if (Snapshot.Observation != WorkerObservation.Connected) {
                UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                    WorkerObservation.WorkerStarting, false, Snapshot.WorkerSnapshot,
                    $"Worker 进程已验证：PID={workerPid}，SessionId={launch.SessionId}；等待 admission + fresh Snapshot"));
                changed = true;
            }
        }

        if (recordToPersist is not null) {
            try {
                _store.SaveRecord(recordToPersist);
            } catch (Exception exception) {
                _logger.Warn(
                    $"Worker PID={workerPid} 已在内存中验证，但写回 Admission Record 失败；"
                    + "Pipe admission 仍将继续。 ",
                    exception);
            }
        }
        if (changed) {
            RaiseStateChanged();
        }
    }

    internal async Task<RunStartResponse> StartRunAsync(RunStartAttempt attempt, CancellationToken cancellationToken = default)
    {
        EnsureStartAllowed(attempt.Plan.RuntimeProfileDigest);
        return await SendRequestAsync<RunStartRequest, RunStartResponse>(
            ProtocolOperations.RunStart,
            new RunStartRequest(attempt.RunId, attempt.PlanDigest, attempt.Plan),
            cancellationToken);
    }

    internal async Task<RunStopResponse> StopRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<RunStopRequest, RunStopResponse>(
            ProtocolOperations.RunStop,
            new RunStopRequest(runId),
            cancellationToken);
    }

    internal async Task<PreviewGetLatestResponse> GetLatestPreviewAsync(Guid runId, long afterRevision, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        Guid workerInstanceId;
        lock (_gate) {
            var worker = Snapshot.WorkerSnapshot;
            if (Snapshot.Observation != WorkerObservation.Connected || !Snapshot.SnapshotFresh
                || worker?.ActiveRun?.RunId != runId) {
                throw new InvalidOperationException("当前 Worker/Snapshot/Run 状态不允许读取 Preview。 ");
            }
            workerInstanceId = worker.WorkerInstanceId;
        }

        var response = await SendRequestAsync<PreviewGetLatestRequest, PreviewGetLatestResponse>(
            ProtocolOperations.PreviewGetLatest, new PreviewGetLatestRequest(runId, afterRevision), cancellationToken);
        ValidatePreviewResponse(response, workerInstanceId, runId, afterRevision);
        return response;
    }

    internal void ChildSessionEnded()
    {
        WorkerAdmissionRecord? record;
        lock (_logDispatchGate) {
            lock (_gate) {
                record = _admission;
                _admission = null;
                _connection = null;
                _awaitedFreshSnapshot = null;
                _logRecoveryGeneration++;
                _logRecoveryTask = null;
                UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                    WorkerObservation.ChildSessionEnded, false, Snapshot.WorkerSnapshot,
                    "Child Session 已结束"));
            }
        }
        if (record is not null) {
            _store.DeleteManifest(record.WorkerInstanceId);
        }
        _store.DeleteRecord();
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        CancelPendingRequests();
        try {
            await _serverTask;
        } catch (OperationCanceledException) {
        }
        _shutdown.Dispose();
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested) {
            await using var server = CreatePipeServer();
            _serverReady.TrySetResult(true);
            try {
                await server.WaitForConnectionAsync(cancellationToken);
                await ServeConnectionAsync(server, cancellationToken);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return;
            } catch (Exception exception) when (exception is IOException
                                                     or ProtocolException
                                                     or UnauthorizedAccessException
                                                     or InvalidOperationException) {
                _logger.Warn($"Worker IPC connection 结束：{exception.GetBaseException().Message}");
                MarkDisconnected();
            }
        }
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        if (!_usePipeAcl) {
            return new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut,
                maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 64 * 1024, outBufferSize: 64 * 1024);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。 ");
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var security = new PipeSecurity();
        security.SetOwner(userSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(networkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 64 * 1024, outBufferSize: 64 * 1024, security);
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using var connection = new ProtocolConnection(server);
        var open = await connection.ReadAsync(cancellationToken)
                   ?? throw new EndOfStreamException("Worker 未发送 connection.open。 ");
        var requestId = open.RequestId
                        ?? throw new ProtocolException("connection.open 缺少 requestId。 ");
        if (open.ProtocolVersion != ProtocolConstants.ProtocolVersion
            || open.MessageType != ProtocolMessageTypes.Request
            || open.Operation != ProtocolOperations.ConnectionOpen) {
            await connection.WriteAsync(
                WireEnvelope.Failure(
                    ProtocolOperations.ConnectionOpen, requestId, "protocol_version_mismatch",
                    "connection.open envelope 不兼容。 "),
                cancellationToken);
            return;
        }

        var payload = ProtocolJson.Deserialize<ConnectionOpenRequest>(open.Data);
        var clientPid = GetClientPid(server);
        var admission = ValidateAdmission(payload, clientPid);
        await connection.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.ConnectionOpen, requestId,
                new ConnectionOpenResponse(true, admission.WorkerInstanceId, clientPid, admission.ChildSessionId)),
            cancellationToken);

        int logRecoveryGeneration;
        lock (_logDispatchGate) {
            lock (_gate) {
                if (admission.WorkerPid != clientPid) {
                    admission = admission with { WorkerPid = clientPid };
                    _admission = admission;
                    _store.SaveRecord(admission);
                }
                _connection = connection;
                _logSequence.BeginWorkerInstance(admission.WorkerInstanceId);
                _logRecoveryGeneration++;
                _logRecoveryTask = null;
                logRecoveryGeneration = _logRecoveryGeneration;
                UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                    WorkerObservation.Connected, false, Snapshot.WorkerSnapshot,
                    "Worker 已接纳，正在同步 Snapshot"));
            }
        }
        RaiseStateChanged();

        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = ReadLoopAsync(
            connection, admission.WorkerInstanceId,
            logRecoveryGeneration, connectionLifetime.Token);
        _ = reader.ContinueWith(
            _ => connectionLifetime.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try {
            var response = await SendRequestAsync<object, GetSnapshotResponse>(
                ProtocolOperations.WorkerGetSnapshot, new { }, connectionLifetime.Token);
            ApplyFreshSnapshot(response.Snapshot, admission);
            await EnsureLogRecoveryAsync(
                admission.WorkerInstanceId, logRecoveryGeneration,
                response.Snapshot.LastLogSequence, connectionLifetime.Token);
            await reader;
        } finally {
            connectionLifetime.Cancel();
            lock (_logDispatchGate) {
                lock (_gate) {
                    if (ReferenceEquals(_connection, connection)) {
                        _connection = null;
                        _logRecoveryGeneration++;
                        _logRecoveryTask = null;
                    }
                }
            }
            CancelPendingRequests();
        }
    }

    private async Task ReadLoopAsync(
        ProtocolConnection connection, Guid workerInstanceId,
        int logRecoveryGeneration, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested) {
            var envelope = await connection.ReadAsync(cancellationToken);
            if (envelope is null) {
                return;
            }
            if (envelope.MessageType == ProtocolMessageTypes.Response && envelope.RequestId is Guid requestId) {
                if (_pending.TryRemove(requestId, out var pending)) {
                    pending.TrySetResult(envelope);
                    continue;
                }
                if (_abandonedRequests.TryRemove(requestId, out _)) {
                    continue;
                }
            }
            if (envelope.MessageType != ProtocolMessageTypes.Event) {
                throw new ProtocolException("GUI 收到无法关联的非 Event envelope。 ");
            }
            switch (envelope.Operation) {
                case ProtocolOperations.WorkerStateChanged:
                case ProtocolOperations.RunStateChanged: {
                        var state = ProtocolJson.Deserialize<StateChangedEvent>(envelope.Data);
                        if (state.WorkerInstanceId != workerInstanceId) {
                            throw new ProtocolException("stateChanged workerInstanceId 不匹配。 ");
                        }
                        ApplyStateEvent(state);
                        break;
                    }
                case ProtocolOperations.LogEntry: {
                        var log = ProtocolJson.Deserialize<LogEntryEvent>(envelope.Data);
                        if (log.WorkerInstanceId != workerInstanceId) {
                            throw new ProtocolException("log.entry workerInstanceId 不匹配。 ");
                        }
                        ApplyLiveLog(workerInstanceId, logRecoveryGeneration, log.Entry, cancellationToken);
                        break;
                    }
            }
        }
    }

    private WorkerAdmissionRecord ValidateAdmission(ConnectionOpenRequest request, int clientPid)
    {
        WorkerAdmissionRecord admission;
        lock (_gate) {
            admission = _admission
                        ?? throw new UnauthorizedAccessException("没有 Pending/valid Admission Record。 ");
        }
        byte[] receivedToken;
        try {
            receivedToken = Convert.FromHexString(request.LaunchToken);
        } catch (Exception exception) when (exception is FormatException or ArgumentNullException) {
            throw new UnauthorizedAccessException("Worker Launch Token 格式非法。 ", exception);
        }
        var expectedToken = Convert.FromHexString(admission.LaunchToken);
        if (request.WorkerInstanceId != admission.WorkerInstanceId || !CryptographicOperations.FixedTimeEquals(receivedToken, expectedToken)
            || request.RuntimeProfileDigest != admission.RuntimeProfileDigest) {
            throw new UnauthorizedAccessException("Worker identity/token/runtime profile 不匹配。 ");
        }
        if (admission.WorkerPid is not null && admission.WorkerPid != clientPid) {
            throw new UnauthorizedAccessException("Pipe client PID 与 Admission Record 不匹配。 ");
        }
        using var process = Process.GetProcessById(clientPid);
        if ((uint)process.SessionId != admission.ChildSessionId) {
            throw new UnauthorizedAccessException("Worker 真实 SessionId 不匹配。 ");
        }
        var imagePath = process.MainModule?.FileName
                        ?? throw new UnauthorizedAccessException("无法取得 Worker 映像路径。 ");
        if (!string.Equals(Path.GetFullPath(imagePath), _workerExecutablePath, StringComparison.OrdinalIgnoreCase)) {
            throw new UnauthorizedAccessException("Worker 映像不来自当前 NarutoAutoGUI 发布包。 ");
        }
        return admission;
    }

    private void ApplyFreshSnapshot(WorkerSnapshot snapshot, WorkerAdmissionRecord admission)
    {
        ValidateSnapshot(snapshot, admission);
        lock (_gate) {
            if (_admission?.WorkerInstanceId != admission.WorkerInstanceId) {
                return;
            }
            if (Snapshot.WorkerSnapshot is { } current && current.WorkerInstanceId == snapshot.WorkerInstanceId
                && current.StateRevision > snapshot.StateRevision) {
                _awaitedFreshSnapshot?.TrySetResult(current);
                _awaitedFreshSnapshot = null;
                return;
            }
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.Connected, true, snapshot,
                "Worker Snapshot 已同步"));
            _awaitedFreshSnapshot?.TrySetResult(snapshot);
            _awaitedFreshSnapshot = null;
        }
        RaiseStateChanged();
    }

    private void ApplyStateEvent(StateChangedEvent state)
    {
        WorkerAdmissionRecord admission;
        lock (_gate) {
            admission = _admission
                        ?? throw new ProtocolException("没有 Admission Record 却收到 state event。 ");
            var currentRevision = Snapshot.WorkerSnapshot?.StateRevision ?? 0;
            if (state.StateRevision <= currentRevision) {
                return;
            }
            if (Snapshot.SnapshotFresh && state.StateRevision != currentRevision + 1) {
                UpdateSnapshotLocked(Snapshot with {
                    SnapshotFresh = false,
                    Detail = "stateRevision 断档，等待完整 Snapshot"
                });
                _ = Task.Run(RefreshSnapshotAfterGapAsync);
                return;
            }
            ValidateSnapshot(state.Snapshot, admission);
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.Connected, true, state.Snapshot,
                "Worker 状态已更新"));
        }
        RaiseStateChanged();
    }

    private async Task RefreshSnapshotAfterGapAsync()
    {
        try {
            var response = await SendRequestAsync<object, GetSnapshotResponse>(
                ProtocolOperations.WorkerGetSnapshot, new { }, _shutdown.Token);
            WorkerAdmissionRecord admission;
            lock (_gate) {
                admission = _admission
                            ?? throw new ProtocolException("刷新 Snapshot 时 Admission Record 已失效。 ");
            }
            ApplyFreshSnapshot(response.Snapshot, admission);
        } catch (Exception exception) {
            _logger.Warn($"刷新 Worker Snapshot 失败：{exception.GetBaseException().Message}");
        }
    }

    private void ApplyLiveLog(
        Guid workerInstanceId, int logRecoveryGeneration, WorkerLogEntry entry, CancellationToken cancellationToken)
    {
        var disposition = ApplyObservedLog(workerInstanceId, logRecoveryGeneration, entry);
        if (disposition == WorkerLogSequenceDisposition.Gap) {
            _ = EnsureLogRecoveryAsync(workerInstanceId, logRecoveryGeneration, entry.Sequence, cancellationToken);
        }
    }

    private Task EnsureLogRecoveryAsync(
        Guid workerInstanceId, int logRecoveryGeneration, long targetSequence, CancellationToken cancellationToken)
    {
        lock (_gate) {
            if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                return Task.CompletedTask;
            }
            _logSequence.ObserveTarget(targetSequence);
            if (_logSequence.LastContiguousSequence >= _logSequence.HighestObservedSequence) {
                return Task.CompletedTask;
            }
            if (_logRecoveryTask is { IsCompleted: false }) {
                return _logRecoveryTask;
            }

            _logRecoveryTask = Task.Run(
                () => RunLogRecoveryAsync(workerInstanceId, logRecoveryGeneration, cancellationToken),
                CancellationToken.None);
            return _logRecoveryTask;
        }
    }

    private async Task RunLogRecoveryAsync(Guid workerInstanceId, int logRecoveryGeneration, CancellationToken cancellationToken)
    {
        var allowRestart = true;
        try {
            while (true) {
                try {
                    await RecoverLogsAsync(workerInstanceId, logRecoveryGeneration, cancellationToken);
                    return;
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    allowRestart = false;
                    return;
                } catch (Exception exception) {
                    _logger.Warn($"恢复 Worker 日志失败，将重试：{exception.GetBaseException().Message}");
                }

                lock (_gate) {
                    if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)
                        || _logSequence.LastContiguousSequence >= _logSequence.HighestObservedSequence) {
                        return;
                    }
                }
                await Task.Delay(LogRecoveryRetryDelay, cancellationToken);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            allowRestart = false;
        } finally {
            var restart = false;
            lock (_gate) {
                if (IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    _logRecoveryTask = null;
                    restart = allowRestart
                              && _logSequence.LastContiguousSequence < _logSequence.HighestObservedSequence;
                }
            }
            if (restart) {
                _ = EnsureLogRecoveryAsync(
                    workerInstanceId, logRecoveryGeneration, targetSequence: 0, cancellationToken);
            }
        }
    }

    private async Task RecoverLogsAsync(Guid workerInstanceId, int logRecoveryGeneration, CancellationToken cancellationToken)
    {
        while (true) {
            long afterSequence;
            lock (_gate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)
                    || _logSequence.LastContiguousSequence >= _logSequence.HighestObservedSequence) {
                    return;
                }
                afterSequence = _logSequence.LastContiguousSequence;
            }

            var page = await SendRequestAsync<LogGetSinceRequest, LogGetSinceResponse>(
                ProtocolOperations.LogGetSince, new LogGetSinceRequest(afterSequence, 500), cancellationToken);
            string? gapWarning = null;
            lock (_gate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    return;
                }
                _logSequence.ObserveTarget(page.LastLogSequence);
                if (page.Gap) {
                    _logSequence.SkipToFirstAvailable(page.FirstAvailableSequence);
                    gapWarning = $"Worker 日志存在断档：{page.MissingFromSequence}-{page.MissingToSequence}。 ";
                }
            }
            if (gapWarning is not null) {
                _logger.Warn(gapWarning);
            }

            foreach (var entry in page.Entries) {
                ApplyRecoveredLog(workerInstanceId, logRecoveryGeneration, entry);
            }

            lock (_gate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    return;
                }
                if (_logSequence.LastContiguousSequence == afterSequence
                    && _logSequence.LastContiguousSequence < _logSequence.HighestObservedSequence) {
                    throw new ProtocolException("log.getSince 未推进 Log Transport Cursor。 ");
                }
            }
        }
    }

    private void ApplyRecoveredLog(Guid workerInstanceId, int logRecoveryGeneration, WorkerLogEntry entry)
    {
        var disposition = ApplyObservedLog(workerInstanceId, logRecoveryGeneration, entry);
        if (disposition == WorkerLogSequenceDisposition.Gap) {
            throw new ProtocolException($"log.getSince 返回非连续 sequence：{entry.Sequence}。 ");
        }
    }

    private WorkerLogSequenceDisposition? ApplyObservedLog(Guid workerInstanceId, int logRecoveryGeneration, WorkerLogEntry entry)
    {
        WorkerLogSequenceDisposition disposition;
        lock (_logDispatchGate) {
            lock (_gate) {
                if (!IsActiveLogConnectionLocked(workerInstanceId, logRecoveryGeneration)) {
                    return null;
                }
                disposition = _logSequence.Observe(entry.Sequence);
            }
            if (disposition == WorkerLogSequenceDisposition.Contiguous) {
                LogReceived?.Invoke(this, entry);
            }
        }
        return disposition;
    }

    private bool IsActiveLogConnectionLocked(Guid workerInstanceId, int logRecoveryGeneration) =>
        _logRecoveryGeneration == logRecoveryGeneration && _connection is not null
        && _admission?.WorkerInstanceId == workerInstanceId && _logSequence.WorkerInstanceId == workerInstanceId;

    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(string operation, TRequest data, CancellationToken cancellationToken)
    {
        ProtocolConnection connection;
        lock (_gate) {
            connection = _connection
                         ?? throw new InvalidOperationException("Worker IPC 尚未连接。 ");
        }
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<WireEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) {
            throw new InvalidOperationException("无法登记 IPC requestId。 ");
        }
        try {
            await connection.WriteAsync(WireEnvelope.Request(operation, requestId, data), cancellationToken);
            var response = await completion.Task.WaitAsync(RequestTimeout, cancellationToken);
            if (response.Success != true) {
                throw new WorkerProtocolErrorException(
                    response.Error?.Code ?? "internal_error", response.Error?.Message ?? "Worker 返回未知错误。 ");
            }
            return ProtocolJson.Deserialize<TResponse>(response.Data);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            AbandonPendingRequest(requestId);
            throw;
        } catch (TimeoutException) {
            AbandonPendingRequest(requestId);
            throw;
        } catch {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    private void AbandonPendingRequest(Guid requestId)
    {
        _abandonedRequests.TryAdd(requestId, 0);
        if (!_pending.TryRemove(requestId, out _)) {
            _abandonedRequests.TryRemove(requestId, out _);
            return;
        }
        _ = ExpireAbandonedRequestAsync(requestId);
    }

    private async Task ExpireAbandonedRequestAsync(Guid requestId)
    {
        try {
            await Task.Delay(RequestTimeout, _shutdown.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
        } finally {
            _abandonedRequests.TryRemove(requestId, out _);
        }
    }

    private void CancelPendingRequests()
    {
        foreach (var pair in _pending) {
            if (_pending.TryRemove(pair.Key, out var pending)) {
                pending.TrySetCanceled();
            }
        }
        _abandonedRequests.Clear();
    }

    private void EnsureStartAllowed(string desiredRuntimeProfileDigest)
    {
        lock (_gate) {
            var worker = Snapshot.WorkerSnapshot;
            if (Snapshot.Observation != WorkerObservation.Connected || !Snapshot.SnapshotFresh || worker is null
                || worker.WorkerState != WorkerState.Ready || worker.ActiveRun is not null
                || worker.RunState != RunState.Idle || worker.RuntimeProfileDigest != desiredRuntimeProfileDigest) {
                throw new InvalidOperationException("当前 Worker/Snapshot/Run 状态不允许 Start。 ");
            }
        }
    }

    private void ValidateSnapshot(WorkerSnapshot snapshot, WorkerAdmissionRecord admission)
    {
        if (snapshot.SnapshotVersion != ProtocolConstants.SnapshotVersion
            || snapshot.ProtocolVersion != ProtocolConstants.ProtocolVersion
            || snapshot.WorkerInstanceId != admission.WorkerInstanceId || snapshot.WorkerPid != admission.WorkerPid
            || snapshot.ChildSessionId != admission.ChildSessionId || snapshot.RuntimeProfileDigest != admission.RuntimeProfileDigest) {
            throw new ProtocolException("Worker Snapshot identity/schema 与 Admission Record 不一致。 ");
        }
        if ((snapshot.ActiveRun is null) != (snapshot.RunState == RunState.Idle)) {
            throw new ProtocolException("Worker Snapshot activeRun/runState invariant 失败。 ");
        }
        if (snapshot.ActiveRun is not null && snapshot.ActiveRun.State != snapshot.RunState) {
            throw new ProtocolException("Worker Snapshot activeRun.state 与 runState 不一致。 ");
        }
    }

    private static void ValidatePreviewResponse(
        PreviewGetLatestResponse response, Guid workerInstanceId, Guid runId, long afterRevision)
    {
        if (response.WorkerInstanceId != workerInstanceId) {
            throw new ProtocolException("Preview response workerInstanceId 不匹配。 ");
        }

        switch (response.Disposition) {
            case "frame":
                if (response.RunId != runId || response.Revision <= afterRevision
                    || response.SampledAtUtc is not { Kind: DateTimeKind.Utc }
                    || response.PixelWidth is not > 0 || response.PixelHeight is not > 0
                    || response.PixelWidth > ProtocolConstants.MaximumPreviewPixelWidth
                    || response.PixelHeight > ProtocolConstants.MaximumPreviewPixelHeight
                    || response.ContentType != "image/png" || response.PngBytes is not { Length: > 0 } pngBytes
                    || pngBytes.Length > ProtocolConstants.MaximumPreviewPngBytes || response.Reason is not null) {
                    throw new ProtocolException("Preview frame response schema 非法。 ");
                }
                break;
            case "not_modified":
                if (response.RunId != runId || response.Revision > afterRevision
                    || response.SampledAtUtc is not null || response.PixelWidth is not null
                    || response.PixelHeight is not null || response.ContentType is not null
                    || response.PngBytes is not null || response.Reason is not null) {
                    throw new ProtocolException("Preview not_modified response schema 非法。 ");
                }
                break;
            case "unavailable":
                if (response.RunId is not null && response.RunId != runId
                    || response.Revision != 0 || response.SampledAtUtc is not null
                    || response.PixelWidth is not null || response.PixelHeight is not null
                    || response.ContentType is not null || response.PngBytes is not null
                    || string.IsNullOrWhiteSpace(response.Reason)) {
                    throw new ProtocolException("Preview unavailable response schema 非法。 ");
                }
                break;
            default:
                throw new ProtocolException($"未知 Preview disposition：{response.Disposition}。 ");
        }
    }

    private void MarkDisconnected()
    {
        lock (_gate) {
            if (_admission is null) {
                return;
            }
            var observation = IsExpectedWorkerAlive(_admission)
                ? WorkerObservation.IpcDisconnected
                : _admission.WorkerPid is null
                    ? WorkerObservation.WorkerStarting
                    : WorkerObservation.WorkerExited;
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                observation, false, Snapshot.WorkerSnapshot,
                observation == WorkerObservation.IpcDisconnected
                    ? "Worker 仍存活，IPC 已断开"
                    : "Worker 进程已退出"));
        }
        RaiseStateChanged();
    }

    private static bool IsExpectedWorkerAlive(WorkerAdmissionRecord admission)
    {
        if (admission.WorkerPid is not int pid) {
            return false;
        }
        try {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (uint)process.SessionId == admission.ChildSessionId;
        } catch {
            return false;
        }
    }

    private void UpdateSnapshotLocked(WorkerCoordinatorSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, Snapshot);

    private static int GetClientPid(NamedPipeServerStream server)
    {
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var processId)) {
            throw new IOException("无法取得 Named Pipe client PID。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        return checked((int)processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);
}

internal sealed class WorkerProtocolErrorException : Exception
{
    internal WorkerProtocolErrorException(string code, string message)
        : base($"{code}: {message}")
    {
    }
}
