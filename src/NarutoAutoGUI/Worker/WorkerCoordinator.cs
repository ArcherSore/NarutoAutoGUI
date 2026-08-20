using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
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
    WorkerObservation Observation,
    bool SnapshotFresh,
    WorkerSnapshot? WorkerSnapshot,
    string Detail)
{
    internal static WorkerCoordinatorSnapshot Empty { get; } = new(
        WorkerObservation.WorkerNotStarted,
        false,
        null,
        "Worker 尚未启动");
}

internal sealed class WorkerCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly AppLogger _logger;
    private readonly WorkerAdmissionStore _store;
    private readonly ChildSessionWorkerLauncher _launcher;
    private readonly string _workerExecutablePath;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<WireEnvelope>> _pending = new();
    private readonly TaskCompletionSource<bool> _serverReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serverTask;
    private ProtocolConnection? _connection;
    private WorkerAdmissionRecord? _admission;
    private TaskCompletionSource<WorkerSnapshot>? _awaitedFreshSnapshot;
    private long _lastReceivedLogSequence;

    internal WorkerCoordinator(AppLogger logger, string stateDirectory, string workerExecutablePath)
    {
        _logger = logger;
        _store = new WorkerAdmissionStore(stateDirectory);
        _launcher = new ChildSessionWorkerLauncher(logger);
        _workerExecutablePath = Path.GetFullPath(workerExecutablePath);
        try
        {
            _admission = _store.Load();
            Snapshot = _admission is null
                ? WorkerCoordinatorSnapshot.Empty
                : new WorkerCoordinatorSnapshot(
                    _admission.WorkerPid is null
                        ? WorkerObservation.WorkerStarting
                        : WorkerObservation.IpcDisconnected,
                    false,
                    null,
                    "已加载 Worker Admission Record，等待 Worker 连接");
        }
        catch (Exception exception)
        {
            Snapshot = new WorkerCoordinatorSnapshot(
                WorkerObservation.WorkerRecoveryConflict,
                false,
                null,
                exception.GetBaseException().Message);
            _logger.Error("读取 Worker Admission Record 失败。", exception);
        }
        _serverTask = Task.Run(() => ServerLoopAsync(_shutdown.Token));
    }

    internal event EventHandler<WorkerCoordinatorSnapshot>? StateChanged;
    internal event EventHandler<WorkerLogEntry>? LogReceived;

    internal WorkerCoordinatorSnapshot Snapshot { get; private set; }

    internal async Task<WorkerSnapshot> PrepareWorkerAsync(
        uint childSessionId,
        ProjectPlanModule project,
        CancellationToken cancellationToken = default)
    {
        await _serverReady.Task.WaitAsync(cancellationToken);
        lock (_gate)
        {
            if (Snapshot.Observation == WorkerObservation.Connected
                && Snapshot.SnapshotFresh
                && Snapshot.WorkerSnapshot?.RuntimeProfileDigest == project.RuntimeProfileDigest)
            {
                return Snapshot.WorkerSnapshot;
            }
            if (_admission is not null)
            {
                throw new InvalidOperationException(
                    "已有 Worker Admission Record；首片尚未提供 Runtime Profile replacement UI。 ");
            }
        }

        var instanceId = Guid.NewGuid();
        var launchToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var manifest = project.CreateLaunchManifest(instanceId);
        var manifestPath = _store.GetManifestPath(instanceId);
        var admission = new WorkerAdmissionRecord(
            instanceId,
            launchToken,
            childSessionId,
            null,
            project.RuntimeProfileDigest,
            DateTime.UtcNow);
        try
        {
            _store.SaveManifest(manifest);
            _store.SaveRecord(admission);
        }
        catch
        {
            _store.DeleteManifest(instanceId);
            throw;
        }

        TaskCompletionSource<WorkerSnapshot> fresh;
        lock (_gate)
        {
            _admission = admission;
            fresh = new TaskCompletionSource<WorkerSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitedFreshSnapshot = fresh;
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.WorkerStarting,
                false,
                null,
                "Pending Admission 已写入，正在启动 Worker"));
        }
        RaiseStateChanged();

        try
        {
            var launch = await _launcher.LaunchAsync(
                childSessionId,
                _workerExecutablePath,
                instanceId,
                launchToken,
                manifestPath,
                cancellationToken);
            RecordVerifiedWorkerProcess(instanceId, launch);
        }
        catch (Exception exception)
        {
            var observedThroughPipe = false;
            lock (_gate)
            {
                observedThroughPipe = _admission is
                {
                    WorkerInstanceId: var currentInstance,
                    WorkerPid: not null
                } current
                                      && currentInstance == instanceId
                                      && IsExpectedWorkerAlive(current);
                if (!observedThroughPipe)
                {
                    _admission = null;
                    _awaitedFreshSnapshot = null;
                    UpdateSnapshotLocked(WorkerCoordinatorSnapshot.Empty);
                }
            }
            if (observedThroughPipe)
            {
                _logger.Warn(
                    "Task Scheduler 进程枚举验证失败，但同一 Worker 已通过 Pipe PID/Session/映像校验；继续等待 fresh Snapshot。",
                    exception);
            }
            else
            {
                _store.DeleteRecord();
                _store.DeleteManifest(instanceId);
                RaiseStateChanged();
                throw;
            }
        }

        try
        {
            var snapshot = await fresh.Task.WaitAsync(AdmissionTimeout, cancellationToken);
            _store.DeleteManifest(instanceId);
            return snapshot;
        }
        catch (TimeoutException exception)
        {
            WorkerAdmissionRecord? timedOutAdmission = null;
            WorkerSnapshot? lateSnapshot = null;
            var rolledBack = false;
            lock (_gate)
            {
                if (Snapshot is
                    {
                        Observation: WorkerObservation.Connected,
                        SnapshotFresh: true,
                        WorkerSnapshot: { WorkerInstanceId: var snapshotInstance } workerSnapshot
                    }
                    && snapshotInstance == instanceId)
                {
                    lateSnapshot = workerSnapshot;
                }
                else if (_admission?.WorkerInstanceId == instanceId)
                {
                    timedOutAdmission = _admission;
                    rolledBack = timedOutAdmission.WorkerPid is null
                                 || !IsExpectedWorkerAlive(timedOutAdmission);
                    _awaitedFreshSnapshot = null;
                    if (rolledBack)
                    {
                        _admission = null;
                        UpdateSnapshotLocked(WorkerCoordinatorSnapshot.Empty with
                        {
                            Detail = "Worker admission 超时且没有存活的已验证进程；Pending Admission 已回滚"
                        });
                    }
                    else
                    {
                        UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                            WorkerObservation.IpcDisconnected,
                            false,
                            Snapshot.WorkerSnapshot,
                            $"Worker PID {timedOutAdmission.WorkerPid} 仍存活，但未按时完成 admission + fresh Snapshot"));
                    }
                }
            }

            if (lateSnapshot is not null)
            {
                _store.DeleteManifest(instanceId);
                _logger.Info("Worker fresh Snapshot 在 admission 超时边界完成；按成功处理。 ");
                return lateSnapshot;
            }

            if (rolledBack)
            {
                _store.DeleteRecord();
                _store.DeleteManifest(instanceId);
                _logger.Warn(
                    $"Worker admission 在 {AdmissionTimeout.TotalSeconds:0} 秒后超时；"
                    + "未发现存活的已验证 Worker，已回滚 worker.json 与 launch manifest。 ");
            }
            else if (timedOutAdmission is not null)
            {
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

    private void RecordVerifiedWorkerProcess(
        Guid workerInstanceId,
        WorkerProcessLaunchResult launch)
    {
        var workerPid = checked((int)launch.ProcessId);
        WorkerAdmissionRecord? recordToPersist = null;
        var changed = false;
        lock (_gate)
        {
            var current = _admission
                          ?? throw new InvalidOperationException("Worker 进程启动后 Admission Record 已不存在。 ");
            if (current.WorkerInstanceId != workerInstanceId)
            {
                throw new InvalidOperationException("Worker 进程启动后 Admission instance 已发生变化。 ");
            }
            if (current.ChildSessionId != launch.SessionId)
            {
                throw new InvalidOperationException("Worker 启动验证返回了错误的 Child Session。 ");
            }
            if (current.WorkerPid is int admittedPid && admittedPid != workerPid)
            {
                throw new InvalidOperationException(
                    $"Task Scheduler 验证 PID={workerPid}，但 Pipe Admission PID={admittedPid}。 ");
            }
            if (current.WorkerPid is null)
            {
                current = current with { WorkerPid = workerPid };
                _admission = current;
                recordToPersist = current;
                changed = true;
            }
            if (Snapshot.Observation != WorkerObservation.Connected)
            {
                UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                    WorkerObservation.WorkerStarting,
                    false,
                    Snapshot.WorkerSnapshot,
                    $"Worker 进程已验证：PID={workerPid}，SessionId={launch.SessionId}；等待 admission + fresh Snapshot"));
                changed = true;
            }
        }

        if (recordToPersist is not null)
        {
            try
            {
                _store.SaveRecord(recordToPersist);
            }
            catch (Exception exception)
            {
                _logger.Warn(
                    $"Worker PID={workerPid} 已在内存中验证，但写回 Admission Record 失败；"
                    + "Pipe admission 仍将继续。 ",
                    exception);
            }
        }
        if (changed)
        {
            RaiseStateChanged();
        }
    }

    internal async Task<RunStartResponse> StartRunAsync(
        RunStartAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        EnsureStartAllowed(attempt.Plan.RuntimeProfileDigest);
        return await SendRequestAsync<RunStartRequest, RunStartResponse>(
            ProtocolOperations.RunStart,
            new RunStartRequest(attempt.RunId, attempt.PlanDigest, attempt.Plan),
            cancellationToken);
    }

    internal async Task<RunStopResponse> StopRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<RunStopRequest, RunStopResponse>(
            ProtocolOperations.RunStop,
            new RunStopRequest(runId),
            cancellationToken);
    }

    internal void ChildSessionEnded()
    {
        WorkerAdmissionRecord? record;
        lock (_gate)
        {
            record = _admission;
            _admission = null;
            _connection = null;
            _awaitedFreshSnapshot = null;
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.ChildSessionEnded,
                false,
                Snapshot.WorkerSnapshot,
                "Child Session 已结束"));
        }
        if (record is not null)
        {
            _store.DeleteManifest(record.WorkerInstanceId);
        }
        _store.DeleteRecord();
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        lock (_gate)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetCanceled();
            }
        }
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        _serverReady.TrySetResult(true);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = CreatePipeServer();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                await ServeConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException
                                                   or ProtocolException
                                                   or UnauthorizedAccessException
                                                   or InvalidOperationException)
            {
                _logger.Warn($"Worker IPC connection 结束：{exception.GetBaseException().Message}");
                MarkDisconnected();
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User
                      ?? throw new InvalidOperationException("\u65e0\u6cd5\u53d6\u5f97\u5f53\u524d Windows \u7528\u6237 SID\u3002 ");
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var security = new PipeSecurity();
        security.SetOwner(userSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            networkSid,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            PipeIdentity.ForCurrentUser(),
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }

    private async Task ServeConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        await using var connection = new ProtocolConnection(server);
        var open = await connection.ReadAsync(cancellationToken)
                   ?? throw new EndOfStreamException("Worker 未发送 connection.open。 ");
        var requestId = open.RequestId
                        ?? throw new ProtocolException("connection.open 缺少 requestId。 ");
        if (open.ProtocolVersion != ProtocolConstants.ProtocolVersion
            || open.MessageType != ProtocolMessageTypes.Request
            || open.Operation != ProtocolOperations.ConnectionOpen)
        {
            await connection.WriteAsync(
                WireEnvelope.Failure(
                    ProtocolOperations.ConnectionOpen,
                    requestId,
                    "protocol_version_mismatch",
                    "connection.open envelope 不兼容。 "),
                cancellationToken);
            return;
        }

        var payload = ProtocolJson.Deserialize<ConnectionOpenRequest>(open.Data);
        var clientPid = GetClientPid(server);
        var admission = ValidateAdmission(payload, clientPid);
        await connection.WriteAsync(
            WireEnvelope.Response(
                ProtocolOperations.ConnectionOpen,
                requestId,
                new ConnectionOpenResponse(
                    true,
                    admission.WorkerInstanceId,
                    clientPid,
                    admission.ChildSessionId)),
            cancellationToken);

        lock (_gate)
        {
            if (admission.WorkerPid != clientPid)
            {
                admission = admission with { WorkerPid = clientPid };
                _admission = admission;
                _store.SaveRecord(admission);
            }
            _connection = connection;
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.Connected,
                false,
                Snapshot.WorkerSnapshot,
                "Worker 已接纳，正在同步 Snapshot"));
        }
        RaiseStateChanged();

        var reader = ReadLoopAsync(connection, admission.WorkerInstanceId, cancellationToken);
        try
        {
            var response = await SendRequestAsync<object, GetSnapshotResponse>(
                ProtocolOperations.WorkerGetSnapshot,
                new { },
                cancellationToken);
            ApplyFreshSnapshot(response.Snapshot, admission);
            await RecoverLogsAsync(response.Snapshot, cancellationToken);
            await reader;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }
        }
    }

    private async Task ReadLoopAsync(
        ProtocolConnection connection,
        Guid workerInstanceId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await connection.ReadAsync(cancellationToken);
            if (envelope is null)
            {
                return;
            }
            if (envelope.MessageType == ProtocolMessageTypes.Response
                && envelope.RequestId is Guid requestId
                && _pending.TryRemove(requestId, out var pending))
            {
                pending.TrySetResult(envelope);
                continue;
            }
            if (envelope.MessageType != ProtocolMessageTypes.Event)
            {
                throw new ProtocolException("GUI 收到无法关联的非 Event envelope。 ");
            }
            switch (envelope.Operation)
            {
                case ProtocolOperations.WorkerStateChanged:
                case ProtocolOperations.RunStateChanged:
                {
                    var state = ProtocolJson.Deserialize<StateChangedEvent>(envelope.Data);
                    if (state.WorkerInstanceId != workerInstanceId)
                    {
                        throw new ProtocolException("stateChanged workerInstanceId 不匹配。 ");
                    }
                    ApplyStateEvent(state);
                    break;
                }
                case ProtocolOperations.LogEntry:
                {
                    var log = ProtocolJson.Deserialize<LogEntryEvent>(envelope.Data);
                    if (log.WorkerInstanceId != workerInstanceId)
                    {
                        throw new ProtocolException("log.entry workerInstanceId 不匹配。 ");
                    }
                    ApplyLog(log.Entry);
                    break;
                }
            }
        }
    }

    private WorkerAdmissionRecord ValidateAdmission(ConnectionOpenRequest request, int clientPid)
    {
        WorkerAdmissionRecord admission;
        lock (_gate)
        {
            admission = _admission
                        ?? throw new UnauthorizedAccessException("没有 Pending/valid Admission Record。 ");
        }
        byte[] receivedToken;
        try
        {
            receivedToken = Convert.FromHexString(request.LaunchToken);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new UnauthorizedAccessException("Worker Launch Token 格式非法。 ", exception);
        }
        var expectedToken = Convert.FromHexString(admission.LaunchToken);
        if (request.WorkerInstanceId != admission.WorkerInstanceId
            || !CryptographicOperations.FixedTimeEquals(receivedToken, expectedToken)
            || request.RuntimeProfileDigest != admission.RuntimeProfileDigest)
        {
            throw new UnauthorizedAccessException("Worker identity/token/runtime profile 不匹配。 ");
        }
        if (admission.WorkerPid is not null && admission.WorkerPid != clientPid)
        {
            throw new UnauthorizedAccessException("Pipe client PID 与 Admission Record 不匹配。 ");
        }
        using var process = Process.GetProcessById(clientPid);
        if ((uint)process.SessionId != admission.ChildSessionId)
        {
            throw new UnauthorizedAccessException("Worker 真实 SessionId 不匹配。 ");
        }
        var imagePath = process.MainModule?.FileName
                        ?? throw new UnauthorizedAccessException("无法取得 Worker 映像路径。 ");
        if (!string.Equals(
                Path.GetFullPath(imagePath),
                _workerExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Worker 映像不来自当前 NarutoAutoGUI 发布包。 ");
        }
        return admission;
    }

    private void ApplyFreshSnapshot(WorkerSnapshot snapshot, WorkerAdmissionRecord admission)
    {
        ValidateSnapshot(snapshot, admission);
        lock (_gate)
        {
            if (_admission?.WorkerInstanceId != admission.WorkerInstanceId)
            {
                return;
            }
            if (Snapshot.WorkerSnapshot is { } current
                && current.WorkerInstanceId == snapshot.WorkerInstanceId
                && current.StateRevision > snapshot.StateRevision)
            {
                _awaitedFreshSnapshot?.TrySetResult(current);
                _awaitedFreshSnapshot = null;
                return;
            }
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.Connected,
                true,
                snapshot,
                "Worker Snapshot 已同步"));
            _awaitedFreshSnapshot?.TrySetResult(snapshot);
            _awaitedFreshSnapshot = null;
        }
        RaiseStateChanged();
    }

    private void ApplyStateEvent(StateChangedEvent state)
    {
        WorkerAdmissionRecord admission;
        lock (_gate)
        {
            admission = _admission
                        ?? throw new ProtocolException("没有 Admission Record 却收到 state event。 ");
            var currentRevision = Snapshot.WorkerSnapshot?.StateRevision ?? 0;
            if (state.StateRevision <= currentRevision)
            {
                return;
            }
            if (Snapshot.SnapshotFresh && state.StateRevision != currentRevision + 1)
            {
                UpdateSnapshotLocked(Snapshot with
                {
                    SnapshotFresh = false,
                    Detail = "stateRevision 断档，等待完整 Snapshot"
                });
                _ = Task.Run(RefreshSnapshotAfterGapAsync);
                return;
            }
            ValidateSnapshot(state.Snapshot, admission);
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                WorkerObservation.Connected,
                true,
                state.Snapshot,
                "Worker 状态已更新"));
        }
        RaiseStateChanged();
    }

    private async Task RefreshSnapshotAfterGapAsync()
    {
        try
        {
            var response = await SendRequestAsync<object, GetSnapshotResponse>(
                ProtocolOperations.WorkerGetSnapshot,
                new { },
                _shutdown.Token);
            WorkerAdmissionRecord admission;
            lock (_gate)
            {
                admission = _admission
                            ?? throw new ProtocolException("刷新 Snapshot 时 Admission Record 已失效。 ");
            }
            ApplyFreshSnapshot(response.Snapshot, admission);
        }
        catch (Exception exception)
        {
            _logger.Warn($"刷新 Worker Snapshot 失败：{exception.GetBaseException().Message}");
        }
    }

    private void ApplyLog(WorkerLogEntry entry)
    {
        lock (_gate)
        {
            if (entry.Sequence <= _lastReceivedLogSequence)
            {
                return;
            }
            _lastReceivedLogSequence = entry.Sequence;
        }
        LogReceived?.Invoke(this, entry);
    }

    private async Task RecoverLogsAsync(WorkerSnapshot snapshot, CancellationToken cancellationToken)
    {
        while (_lastReceivedLogSequence < snapshot.LastLogSequence)
        {
            var page = await SendRequestAsync<LogGetSinceRequest, LogGetSinceResponse>(
                ProtocolOperations.LogGetSince,
                new LogGetSinceRequest(_lastReceivedLogSequence, 500),
                cancellationToken);
            if (page.Gap)
            {
                _logger.Warn(
                    $"Worker 日志存在断档：{page.MissingFromSequence}-{page.MissingToSequence}。 ");
            }
            foreach (var entry in page.Entries)
            {
                ApplyLog(entry);
            }
            if (!page.HasMore || page.Entries.Count == 0)
            {
                break;
            }
        }
    }

    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        string operation,
        TRequest data,
        CancellationToken cancellationToken)
    {
        ProtocolConnection connection;
        lock (_gate)
        {
            connection = _connection
                         ?? throw new InvalidOperationException("Worker IPC 尚未连接。 ");
        }
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<WireEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("无法登记 IPC requestId。 ");
        }
        try
        {
            await connection.WriteAsync(
                WireEnvelope.Request(operation, requestId, data),
                cancellationToken);
            var response = await completion.Task.WaitAsync(RequestTimeout, cancellationToken);
            if (response.Success != true)
            {
                throw new WorkerProtocolErrorException(
                    response.Error?.Code ?? "internal_error",
                    response.Error?.Message ?? "Worker 返回未知错误。 ",
                    response.Error?.Retriable ?? false);
            }
            return ProtocolJson.Deserialize<TResponse>(response.Data);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private void EnsureStartAllowed(string desiredRuntimeProfileDigest)
    {
        lock (_gate)
        {
            var worker = Snapshot.WorkerSnapshot;
            if (Snapshot.Observation != WorkerObservation.Connected
                || !Snapshot.SnapshotFresh
                || worker is null
                || worker.WorkerState != WorkerState.Ready
                || worker.ActiveRun is not null
                || worker.RunState != RunState.Idle
                || worker.RuntimeProfileDigest != desiredRuntimeProfileDigest)
            {
                throw new InvalidOperationException("当前 Worker/Snapshot/Run 状态不允许 Start。 ");
            }
        }
    }

    private void ValidateSnapshot(WorkerSnapshot snapshot, WorkerAdmissionRecord admission)
    {
        if (snapshot.SnapshotVersion != ProtocolConstants.SnapshotVersion
            || snapshot.ProtocolVersion != ProtocolConstants.ProtocolVersion
            || snapshot.WorkerInstanceId != admission.WorkerInstanceId
            || snapshot.WorkerPid != admission.WorkerPid
            || snapshot.ChildSessionId != admission.ChildSessionId
            || snapshot.RuntimeProfileDigest != admission.RuntimeProfileDigest)
        {
            throw new ProtocolException("Worker Snapshot identity/schema 与 Admission Record 不一致。 ");
        }
        if ((snapshot.ActiveRun is null) != (snapshot.RunState == RunState.Idle))
        {
            throw new ProtocolException("Worker Snapshot activeRun/runState invariant 失败。 ");
        }
        if (snapshot.ActiveRun is not null && snapshot.ActiveRun.State != snapshot.RunState)
        {
            throw new ProtocolException("Worker Snapshot activeRun.state 与 runState 不一致。 ");
        }
    }

    private void MarkDisconnected()
    {
        lock (_gate)
        {
            if (_admission is null)
            {
                return;
            }
            var observation = IsExpectedWorkerAlive(_admission)
                ? WorkerObservation.IpcDisconnected
                : _admission.WorkerPid is null
                    ? WorkerObservation.WorkerStarting
                    : WorkerObservation.WorkerExited;
            UpdateSnapshotLocked(new WorkerCoordinatorSnapshot(
                observation,
                false,
                Snapshot.WorkerSnapshot,
                observation == WorkerObservation.IpcDisconnected
                    ? "Worker 仍存活，IPC 已断开"
                    : "Worker 进程已退出"));
        }
        RaiseStateChanged();
    }

    private static bool IsExpectedWorkerAlive(WorkerAdmissionRecord admission)
    {
        if (admission.WorkerPid is not int pid)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (uint)process.SessionId == admission.ChildSessionId;
        }
        catch
        {
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
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var processId))
        {
            throw new IOException(
                "无法取得 Named Pipe client PID。",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
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
    internal WorkerProtocolErrorException(string code, string message, bool retriable)
        : base($"{code}: {message}")
    {
        Code = code;
        Retriable = retriable;
    }

    internal string Code { get; }
    internal bool Retriable { get; }
}
