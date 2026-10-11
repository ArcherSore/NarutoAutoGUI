using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

// The Worker's authority over Worker State and its MaaNOP Runs (ADR-0001). One lock guards every state change, and
// each commit advances stateRevision by one. Plan Items run through IPlanItemExecution, which is never called while
// the lock is held because onRunning arrives on the MaaFramework callback thread and takes the lock.
internal sealed class RunSupervisor
{
    private readonly object _stateGate = new();
    private readonly LaunchManifest _manifest;
    private readonly WorkerLogBuffer _logs;
    private readonly Func<Guid, RunPlanItem, Action, IPlanItemExecution> _createExecution;
    private readonly Action<string, string, string, Guid?> _log;
    private readonly Action<string, WorkerSnapshot> _publish;
    private readonly CancellationToken _shutdown;
    private readonly Dictionary<Guid, (string Digest, RunSnapshot? Terminal)> _ledger = new();
    private WorkerState _workerState = WorkerState.Starting;
    private StructuredReason? _workerReason;
    private DependencyStatus _dependencyStatus;
    private RunSnapshot? _activeRun;
    private RunSnapshot? _lastRun;
    private IPlanItemExecution? _execution;
    private long _stateRevision = 1;

    // publish receives every committed snapshot except Stopping, which the host writes directly after run.stop.
    internal RunSupervisor(
        LaunchManifest manifest, WorkerLogBuffer logs, Func<Guid, RunPlanItem, Action, IPlanItemExecution> createExecution,
        Action<string, string, string, Guid?> log, Action<string, WorkerSnapshot> publish, CancellationToken shutdown)
    {
        _manifest = manifest;
        _logs = logs;
        _createExecution = createExecution;
        _log = log;
        _publish = publish;
        _shutdown = shutdown;
        var unavailable = new DependencyCheck(false, null, "尚未检查");
        _dependencyStatus = new DependencyStatus(
            DateTime.UtcNow,
            typeof(RunSupervisor).Assembly.GetName().Version?.ToString() ?? "unknown",
            "starting",
            unavailable,
            unavailable,
            unavailable,
            unavailable,
            unavailable);
    }

    internal bool IsReady
    {
        get
        {
            lock (_stateGate) {
                return _workerState == WorkerState.Ready;
            }
        }
    }

    internal void CompleteInitialization(DependencyStatus status, StructuredReason? reason)
    {
        WorkerSnapshot snapshot;
        lock (_stateGate) {
            _dependencyStatus = status;
            _workerReason = reason;
            _workerState = reason is null ? WorkerState.Ready : WorkerState.NotReady;
            snapshot = CommitLocked();
        }
        PublishState(ProtocolOperations.WorkerStateChanged, snapshot);
    }

    internal WorkerSnapshot GetSnapshot()
    {
        lock (_stateGate) {
            return GetSnapshotLocked();
        }
    }

    internal RunStartResponse Accept(RunStartRequest request)
    {
        IPlanItemExecution execution;
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
                    return new RunStartResponse("already_accepted");
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
                throw new WorkerRequestException("worker_busy", "Worker 已有 active Run。 ");
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
            _ledger.Add(request.RunId, (request.PlanDigest, null));
            execution = CreateExecution(request.RunId, item);
            _execution = execution;
            snapshot = CommitLocked();
        }

        PublishState(ProtocolOperations.RunStateChanged, snapshot);
        Log(
            "INFO", "run.lifecycle",
            $"Run 已接受：{request.RunId}，items={run.Items.Count}，first={run.Items[0].TaskName}。 ", request.RunId);
        _ = Task.Run(() => ExecuteRunAsync(request.RunId, execution, _shutdown));
        return new RunStartResponse("accepted");
    }

    // Stopping has two phases: the flag is set before run.stop is acknowledged, and MaaFramework Stop starts only when
    // the host calls PendingStop.Begin after writing the Stopping snapshot.
    internal RunStopResponse Stop(RunStopRequest request, out PendingStop? pendingStop)
    {
        pendingStop = null;
        IPlanItemExecution execution;
        WorkerSnapshot snapshot;
        lock (_stateGate) {
            if (_activeRun is null) {
                if (_lastRun?.RunId == request.RunId) {
                    return new RunStopResponse("already_terminal");
                }
                throw new WorkerRequestException("run_id_mismatch", "没有匹配的 active Run。 ");
            }
            if (_activeRun.RunId != request.RunId) {
                throw new WorkerRequestException("run_id_mismatch", "runId 与 active Run 不一致。 ");
            }
            if (_activeRun.State == RunState.Stopping) {
                return new RunStopResponse("already_stopping");
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
            execution = _execution
                        ?? throw new WorkerRequestException("internal_error", "active Run 缺少 execution context。 ");
            snapshot = CommitLocked();
        }

        execution.RequestStop();
        pendingStop = new PendingStop(this, request.RunId, execution, snapshot);
        return new RunStopResponse("stop_requested");
    }

    internal sealed class PendingStop(
        RunSupervisor owner, Guid runId, IPlanItemExecution execution, WorkerSnapshot stoppingSnapshot)
    {
        internal WorkerSnapshot StoppingSnapshot { get; } = stoppingSnapshot;

        internal void Begin() => owner.BeginStop(runId, execution);
    }

    private void BeginStop(Guid runId, IPlanItemExecution execution)
    {
        Log("INFO", "run.stop", $"已接受 run.stop：{runId}。 ", runId);
        _ = Task.Run(async () =>
        {
            try {
                await execution.StopAsync(_shutdown);
            } catch (Exception exception) {
                Log(
                    "ERROR", "run.stop",
                    $"MaaFramework Stop 确认失败：{exception.GetBaseException().Message}",
                    runId);
            }
        });
    }

    private async Task ExecuteRunAsync(
        Guid runId, IPlanItemExecution execution,
        CancellationToken cancellationToken)
    {
        while (true) {
            var result = await execution.ExecuteAsync(cancellationToken);
            IPlanItemExecution? nextExecution = null;
            WorkerSnapshot snapshot;
            lock (_stateGate) {
                if (_activeRun?.RunId != runId || !ReferenceEquals(_execution, execution)) {
                    return;
                }

                var now = DateTime.UtcNow;
                if (result.Outcome == RuntimeExecutionOutcome.StopTimedOut) {
                    _workerState = WorkerState.Faulted;
                    _workerReason = result.Error;
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

    // Called under _stateGate so the active Run is never committed without its execution; factories only construct.
    private IPlanItemExecution CreateExecution(Guid runId, RunPlanItem item) =>
        _createExecution(runId, item, () => MarkRunRunning(runId, item.PlanItemId));

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
            snapshot = CommitLocked();
        }
        PublishState(ProtocolOperations.RunStateChanged, snapshot);
    }

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
            _workerState, _workerReason, _dependencyStatus, _activeRun?.State ?? RunState.Idle,
            _activeRun, _lastRun, firstLog, lastLog);
    }

    private void PublishState(string operation, WorkerSnapshot snapshot) => _publish(operation, snapshot);

    private void Log(string level, string source, string message, Guid? runId = null) =>
        _log(level, source, message, runId);

    private static string GetWorkerVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}
