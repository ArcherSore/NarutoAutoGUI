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
    private const string EndChildSessionHint = "请从托盘菜单选择“结束桌面分身”后重新准备运行环境。";
    private readonly object _gate = new();
    private readonly object _logDispatchGate = new();
    private readonly AppLogger _logger;
    private readonly WorkerAdmissionStore _store;
    private readonly string _workerExecutablePath;
    private readonly string _workerImageName;
    private readonly string _pipeName;
    private readonly bool _usePipeAcl;
    private readonly Func<IReadOnlyList<WorkerProcessEntry>> _captureProcesses;
    private readonly Func<IReadOnlyCollection<uint>> _enumerateSessions;
    private readonly Func<uint, Guid, string, string, CancellationToken, Task<VerifiedChildSessionProcessLaunch>>
        _launchWorker;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<WireEnvelope>> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte> _abandonedRequests = new();
    private readonly TaskCompletionSource<bool> _serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serverTask;
    private ProtocolConnection? _connection;
    private WorkerAdmissionRecord? _admission;
    // An unreadable record may still describe a live Worker, so it blocks Prepare until the session is ended.
    private bool _admissionUnreadable;
    private Guid? _launchingWorkerInstanceId;
    private TaskCompletionSource<WorkerSnapshot>? _awaitedFreshSnapshot;
    private readonly WorkerLogSequenceTracker _logSequence = new();
    private Task? _logRecoveryTask;
    private int _logRecoveryGeneration;

    internal WorkerCoordinator(AppLogger logger, string stateDirectory, string workerExecutablePath)
        : this(logger, stateDirectory, workerExecutablePath, PipeIdentity.ForCurrentUser(), usePipeAcl: true)
    {
    }

    // The optional delegates are self-test seams; production always inspects and launches for real.
    internal WorkerCoordinator(
        AppLogger logger, string stateDirectory, string workerExecutablePath,
        string pipeName, bool usePipeAcl,
        Func<IReadOnlyList<WorkerProcessEntry>>? captureProcesses = null,
        Func<IReadOnlyCollection<uint>>? enumerateSessions = null,
        Func<uint, Guid, string, string, CancellationToken, Task<VerifiedChildSessionProcessLaunch>>?
            launchWorker = null)
    {
        _logger = logger;
        _store = new WorkerAdmissionStore(stateDirectory);
        _workerExecutablePath = Path.GetFullPath(workerExecutablePath);
        _workerImageName = Path.GetFileName(_workerExecutablePath);
        _pipeName = pipeName;
        _usePipeAcl = usePipeAcl;
        _captureProcesses = captureProcesses ?? WorkerAdmissionInspection.CaptureProcesses;
        _enumerateSessions = enumerateSessions ?? WorkerAdmissionInspection.EnumerateSessionIds;
        var launcher = new ChildSessionWorkerLauncher(logger);
        _launchWorker = launchWorker ?? ((sessionId, instanceId, launchToken, manifestPath, cancellationToken) =>
            launcher.LaunchAsync(
                sessionId, _workerExecutablePath, instanceId, launchToken, manifestPath, cancellationToken));
        // Stale recovery runs before the pipe accepts connections; a kept record still lets its Worker reconnect.
        Snapshot = WorkerCoordinatorSnapshot.Empty;
        LoadAdmission();
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

    internal int? TrackedWorkerPid
    {
        get
        {
            lock (_gate) {
                return _admission?.WorkerPid;
            }
        }
    }

    internal Task WaitForServerReadyAsync(CancellationToken cancellationToken) =>
        _serverReady.Task.WaitAsync(cancellationToken);

    internal async Task<WorkerSnapshot> PrepareWorkerAsync(
        uint childSessionId, ProjectPlanModule project, CancellationToken cancellationToken = default)
    {
        await WaitForServerReadyAsync(cancellationToken);
        WorkerAdmissionRecord? existing;
        lock (_gate) {
            if (Snapshot.Observation == WorkerObservation.Connected && Snapshot.SnapshotFresh
                && Snapshot.WorkerSnapshot?.RuntimeProfileDigest == project.RuntimeProfileDigest) {
                return Snapshot.WorkerSnapshot;
            }
            if (_admissionUnreadable) {
                throw new InvalidOperationException(
                    $"无法读取 Worker Admission Record，为避免重复启动 Worker 已停止准备。{EndChildSessionHint}");
            }
            if (_connection is not null) {
                throw new InvalidOperationException(Snapshot.SnapshotFresh
                    ? $"当前 Worker 使用的 MaaNOP 项目与已加载的项目不一致。{EndChildSessionHint}"
                    : "Worker 正在同步状态，请稍后重试。");
            }
            existing = _admission;
        }
        if (existing is not null) {
            var liveness = ClassifyAdmission(existing);
            if (liveness != AdmissionLiveness.Stale
                || !TryDiscardStaleAdmission(existing, "上次的 Worker 已不存在，Admission Record 已清理")) {
                throw CreateAdmissionRefusal(existing, liveness);
            }
            _logger.Info($"已证明 Worker instance={existing.WorkerInstanceId} 不再存在，已清理其 Admission Record。 ");
        }
        // Predictable failures and an occupied session are rejected before any Admission is written.
        ValidateWorkerExecutable();
        EnsureSessionHasNoWorker(childSessionId);

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
            _launchingWorkerInstanceId = instanceId;
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
            VerifiedChildSessionProcessLaunch launch;
            try {
                launch = await _launchWorker(childSessionId, instanceId, launchToken, manifestPath, cancellationToken);
            } finally {
                lock (_gate) {
                    _launchingWorkerInstanceId = null;
                }
            }
            RecordVerifiedWorkerProcess(instanceId, launch);
        } catch (Exception exception) {
            WorkerAdmissionRecord? current;
            lock (_gate) {
                current = _admission?.WorkerInstanceId == instanceId ? _admission : null;
            }
            if (current is null) {
                throw;
            }
            // RunEx may already have been submitted, so a launch exception alone does not prove the Worker absent.
            var liveness = ClassifyAdmission(current);
            if (liveness == AdmissionLiveness.Alive && current.WorkerPid is not null) {
                _logger.Warn(
                    "Task Scheduler 进程枚举验证失败，但同一 Worker 已通过 Pipe PID/Session/映像校验；"
                    + "继续等待 fresh Snapshot。",
                    exception);
            } else {
                if (liveness == AdmissionLiveness.Stale
                    && TryDiscardStaleAdmission(current, "Worker 启动失败且已证明没有存活的 Worker；Admission 已回滚")) {
                    _logger.Warn("Worker 启动失败，且已证明没有存活的 Worker；已回滚 worker.json 与 launch manifest。 ");
                } else {
                    KeepUnprovenAdmission(current, "Worker 启动未确认；为避免重复启动，Admission 已保留");
                    _logger.Warn("Worker 启动未确认，且无法证明 Worker 不会出现；保留 Admission，结束桌面分身前不再启动。 ");
                }
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
                    _awaitedFreshSnapshot = null;
                }
            }

            if (lateSnapshot is not null) {
                _store.DeleteManifest(instanceId);
                _logger.Info("Worker fresh Snapshot 在 admission 超时边界完成；按成功处理。 ");
                return lateSnapshot;
            }

            var timeout = $"Worker admission 在 {AdmissionTimeout.TotalSeconds:0} 秒后超时；";
            string disposition;
            var liveness = timedOutAdmission is null ? (AdmissionLiveness?)null : ClassifyAdmission(timedOutAdmission);
            if (timedOutAdmission is null) {
                disposition = "Admission 已由 Child Session 生命周期清理。 ";
            } else if (liveness == AdmissionLiveness.Stale && TryDiscardStaleAdmission(
                timedOutAdmission, "Worker admission 超时且没有存活的已验证进程；Pending Admission 已回滚")) {
                _logger.Warn($"{timeout}已证明没有存活的 Worker，已回滚 worker.json 与 launch manifest。 ");
                disposition = "未发现存活的已验证 Worker，Pending Admission 已自动回滚。 ";
            } else if (liveness == AdmissionLiveness.Alive) {
                SetDisconnectedObservation(timedOutAdmission, WorkerObservation.IpcDisconnected,
                    $"Worker PID {timedOutAdmission.WorkerPid} 仍存活，但未按时完成 admission + fresh Snapshot");
                _logger.Warn(
                    $"{timeout}PID={timedOutAdmission.WorkerPid}、SessionId={timedOutAdmission.ChildSessionId} 仍存活，"
                    + "保留 Admission 供 Worker 重连。 ");
                disposition = "已验证 Worker 仍存活，Admission 已保留供重连。 ";
            } else {
                KeepUnprovenAdmission(timedOutAdmission, "Worker admission 超时，且无法确认 Worker 是否仍在运行");
                _logger.Warn($"{timeout}无法确认 Worker 是否仍在运行，保留 Admission。 ");
                disposition = $"无法确认 Worker 是否仍在运行，Admission 已保留。{EndChildSessionHint}";
            }
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

    internal async Task<PreviewResponse> SendPreviewAsync(
        string operation, PreviewRequest request, CancellationToken cancellationToken = default)
    {
        uint sessionId;
        int workerPid;
        lock (_gate) {
            var worker = Snapshot.WorkerSnapshot;
            if (Snapshot.Observation != WorkerObservation.Connected || !Snapshot.SnapshotFresh
                || worker?.WorkerInstanceId != request.WorkerInstanceId) {
                throw new InvalidOperationException("当前 Worker/Snapshot 不允许预览。");
            }
            sessionId = worker.ChildSessionId;
            workerPid = worker.WorkerPid;
        }
        var response = await SendRequestAsync<PreviewRequest, PreviewResponse>(operation, request, cancellationToken);
        var expected = new PreviewIdentity(request.WorkerInstanceId, sessionId, request.SubscriptionId);
        if (response.Identity != expected || !Enum.IsDefined(response.State) || response.Generation < 0) {
            throw new ProtocolException("Preview response identity/state 非法。");
        }
        if (response.Descriptor is { } descriptor) {
            PreviewBuffer.ValidateDescriptor(descriptor);
            if (descriptor.Identity != expected || descriptor.OwnerPid != workerPid) {
                throw new ProtocolException("Preview descriptor 不属于当前 Worker。");
            }
        }
        return response;
    }

    internal void ChildSessionEnded()
    {
        WorkerAdmissionRecord? record;
        lock (_logDispatchGate) {
            lock (_gate) {
                record = _admission;
                _admission = null;
                _admissionUnreadable = false;
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
        // Validation ran outside the gate; a record removed or replaced meanwhile is never acknowledged or revived.
        lock (_gate) {
            _ = RequireCurrentAdmissionLocked(admission, clientPid);
        }
        await connection.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.ConnectionOpen, requestId,
                new { }),
            cancellationToken);

        int logRecoveryGeneration;
        lock (_logDispatchGate) {
            lock (_gate) {
                admission = RequireCurrentAdmissionLocked(admission, clientPid);
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

    // Presentation only: a disconnect never deletes the Admission.
    private void MarkDisconnected()
    {
        WorkerAdmissionRecord? admission;
        lock (_gate) {
            admission = _admission;
        }
        if (admission is null) {
            return;
        }
        if (admission.WorkerPid is null) {
            SetDisconnectedObservation(admission, WorkerObservation.WorkerStarting, "等待 Worker 连接");
            return;
        }
        var liveness = ClassifyAdmission(admission);
        SetDisconnectedObservation(admission,
            liveness == AdmissionLiveness.Stale ? WorkerObservation.WorkerExited : WorkerObservation.IpcDisconnected,
            liveness switch {
                AdmissionLiveness.Stale => "Worker 进程已退出",
                AdmissionLiveness.Alive => "Worker 仍存活，IPC 已断开",
                _ => "无法确认 Worker 是否仍在运行，IPC 已断开"
            });
    }

    private void LoadAdmission()
    {
        WorkerAdmissionRecord? record;
        try {
            record = _store.Load();
        } catch (Exception exception) {
            _admissionUnreadable = true;
            Snapshot = new WorkerCoordinatorSnapshot(
                WorkerObservation.WorkerRecoveryConflict, false, null,
                $"无法读取 Worker Admission Record：{exception.GetBaseException().Message}");
            _logger.Error("读取 Worker Admission Record 失败；结束桌面分身前不会启动新的 Worker。", exception);
            return;
        }
        if (record is null) {
            return;
        }
        _admission = record;
        var liveness = ClassifyAdmission(record);
        if (liveness == AdmissionLiveness.Stale
            && TryDiscardStaleAdmission(record, "上次的 Worker 已不存在，Admission Record 已清理")) {
            _logger.Info($"启动时已证明 Worker instance={record.WorkerInstanceId} 不再存在，已清理其 Admission Record。 ");
            return;
        }
        if (liveness == AdmissionLiveness.Alive) {
            Snapshot = new WorkerCoordinatorSnapshot(
                record.WorkerPid is null ? WorkerObservation.WorkerStarting : WorkerObservation.IpcDisconnected,
                false, null, "已加载 Worker Admission Record，等待 Worker 连接");
            return;
        }
        Snapshot = new WorkerCoordinatorSnapshot(
            WorkerObservation.WorkerRecoveryConflict, false, null,
            "无法确认上次的 Worker 是否仍在运行；Admission 已保留，等待 Worker 连接或结束桌面分身");
        _logger.Warn($"无法确认 Worker instance={record.WorkerInstanceId} 是否仍在运行；保留 Admission Record。 ");
    }

    private AdmissionLiveness ClassifyAdmission(WorkerAdmissionRecord record)
    {
        lock (_gate) {
            if (_launchingWorkerInstanceId == record.WorkerInstanceId
                || _connection is not null && _admission?.WorkerInstanceId == record.WorkerInstanceId) {
                return AdmissionLiveness.Alive;
            }
        }
        return WorkerAdmissionInspection.Classify(
            record, _workerImageName, DateTime.UtcNow, _captureProcesses, _enumerateSessions);
    }

    // Deletes only the record that was classified, and only while nothing has connected or launched for it since.
    private bool TryDiscardStaleAdmission(WorkerAdmissionRecord record, string detail)
    {
        lock (_gate) {
            if (!ReferenceEquals(_admission, record) || _connection is not null
                || _launchingWorkerInstanceId == record.WorkerInstanceId) {
                return false;
            }
            try {
                _store.DeleteRecord();
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _logger.Warn("删除失效的 Worker Admission Record 失败；保留该记录。", exception);
                return false;
            }
            _admission = null;
            _awaitedFreshSnapshot = null;
            UpdateSnapshotLocked(WorkerCoordinatorSnapshot.Empty with { Detail = detail });
            try {
                _store.DeleteManifest(record.WorkerInstanceId);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _logger.Warn("删除失效 Worker 的 launch manifest 失败。", exception);
            }
        }
        RaiseStateChanged();
        return true;
    }

    private void KeepUnprovenAdmission(WorkerAdmissionRecord record, string detail)
    {
        lock (_gate) {
            if (ReferenceEquals(_admission, record)) {
                _awaitedFreshSnapshot = null;
            }
        }
        SetDisconnectedObservation(record, WorkerObservation.WorkerRecoveryConflict, detail);
    }

    private void SetDisconnectedObservation(WorkerAdmissionRecord record, WorkerObservation observation, string detail)
    {
        lock (_gate) {
            if (!ReferenceEquals(_admission, record) || _connection is not null) {
                return;
            }
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(observation, false, Snapshot.WorkerSnapshot, detail));
        }
        RaiseStateChanged();
    }

    private static InvalidOperationException CreateAdmissionRefusal(
        WorkerAdmissionRecord record, AdmissionLiveness liveness) => new(liveness switch {
            AdmissionLiveness.Alive when record.WorkerPid is int pid =>
                $"桌面分身中的 Worker（PID {pid}）仍在运行，正在等待它重新连接。请稍后重试；如仍无法恢复，{EndChildSessionHint}",
            AdmissionLiveness.Alive => "Worker 正在启动，请稍后重试。",
            AdmissionLiveness.Stale => "上次的 Worker 已不存在，但暂时无法清理其 Admission Record，请稍后重试。",
            _ => $"无法确认上一次启动的 Worker 是否已退出，为避免重复启动 Worker 已停止准备。{EndChildSessionHint}"
        });

    private void ValidateWorkerExecutable()
    {
        if (!File.Exists(_workerExecutablePath)) {
            throw new FileNotFoundException("NarutoAutoWorker 尚未随 GUI 发布。请重新运行正式发布脚本。", _workerExecutablePath);
        }
        if (!string.Equals(Path.GetExtension(_workerExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException($"Worker 可执行文件无效：{_workerExecutablePath}。 ");
        }
    }

    // The primary guard against a second live Worker; the Worker's per-session mutex is only a backstop.
    private void EnsureSessionHasNoWorker(uint childSessionId)
    {
        IReadOnlyList<WorkerProcessEntry> processes;
        try {
            processes = _captureProcesses();
        } catch (Exception exception) {
            throw new InvalidOperationException(
                "无法枚举进程以确认桌面分身中没有 Worker，为避免重复启动 Worker 已停止准备。请稍后重试。", exception);
        }
        var (running, unreadable) = WorkerAdmissionInspection.FindWorkers(processes, childSessionId, _workerImageName);
        if (running.Length > 0) {
            throw new InvalidOperationException(
                $"桌面分身中已有 {_workerImageName} 进程（PID {string.Join("、", running)}），"
                + $"为避免重复启动 Worker 已停止准备。{EndChildSessionHint}");
        }
        if (unreadable.Length > 0) {
            throw new InvalidOperationException(
                $"无法读取进程 {string.Join("、", unreadable)} 的信息，不能确认桌面分身中没有 Worker，"
                + $"为避免重复启动 Worker 已停止准备。请稍后重试；如仍失败，{EndChildSessionHint}");
        }
    }

    private WorkerAdmissionRecord RequireCurrentAdmissionLocked(WorkerAdmissionRecord validated, int clientPid)
    {
        if (_admission is not { } current || current.WorkerInstanceId != validated.WorkerInstanceId) {
            throw new UnauthorizedAccessException("Worker Admission 已被移除或替换。 ");
        }
        if (current.WorkerPid is int pid && pid != clientPid) {
            throw new UnauthorizedAccessException("Pipe client PID 与 Admission Record 不匹配。 ");
        }
        return current;
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
