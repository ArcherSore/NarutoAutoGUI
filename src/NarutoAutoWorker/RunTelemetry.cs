using System.Diagnostics;
using System.Text.Json;
using MaaFramework.Binding.Notification;
using NarutoAutoGUI.Protocol;
using Sentry;
using Sentry.Extensibility;

namespace NarutoAutoWorker;

// The Worker's side of telemetry (ADR 0009). Every member is a side path: it never throws to its caller, never
// blocks a Run, and a broken SDK only turns telemetry off.
internal interface IRunTelemetry : IDisposable
{
    void SetEnabled(bool enabled);

    // The passwords a Run uses, registered before the Run is accepted so nothing it causes can carry them out.
    void Register(IReadOnlyList<string> redactionValues);

    // Called with every published Worker snapshot, in no particular thread.
    void Observe(WorkerSnapshot snapshot);

    // A MaaFramework callback of a Plan Item, with the trace its focus declares (null when it declares none).
    void OnTaskerEvent(Guid planItemId, string message, string details, bool? trace);

    // An exception that ends the Worker process.
    void CaptureUnhandled(Exception exception);

    // The Agent process of a Plan Item, whose lines in maafw.log belong to that item's evidence.
    void AgentStarted(Guid planItemId, int processId);
}

internal sealed class RunTelemetry : IRunTelemetry
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    // Each Sentry Log carries at most this much text.
    private const int LogChunkCharacters = 6000;
    // The SDK keeps at most this many Spans in a Transaction; node Spans leave room for every Plan Item's own.
    private const int MaximumSpans = 1000;
    private readonly object _gate = new();
    private readonly Action<string, string, string> _log;
    private readonly IDisposable _sdk;
    private readonly TelemetryScrubber _scrubber = new();
    private readonly Dictionary<Guid, ISpan> _spans = [];
    private readonly HashSet<Guid> _finished = [];
    private readonly Dictionary<Guid, PlanItemTrace> _traces = [];
    private readonly string _project;
    private readonly string _debugDirectory;
    private readonly double _attachmentRate;
    private readonly Func<Guid, IReadOnlyList<WorkerLogEntry>> _itemLogs;
    private readonly HashSet<PendingFailure> _pending = [];
    private volatile bool _enabled;
    private WorkerState? _workerState;
    // Only a Run that started while telemetry was on is reported whole.
    private DateTime _enabledSinceUtc = DateTime.MinValue;
    private long _revision;
    private Guid? _runId;
    private ITransactionTracer? _transaction;
    private int _nodeSpanBudget;
    private int _faultLogged;

    internal static IRunTelemetry None => Disabled.Instance;

    // A self-test may only send to the transport it supplies.
    internal static bool SelfTestProcess { get; set; }

    private RunTelemetry(LaunchManifest manifest, TelemetryOptions options, Action<string, string, string> log,
        ITransport? transport, Func<Guid, IReadOnlyList<WorkerLogEntry>> itemLogs)
    {
        _log = log;
        _project = manifest.Project.Name;
        // MaaFramework's LogDir, where maafw.log and the failure screenshots are written.
        _debugDirectory = Path.Combine(manifest.ProjectRoot, "debug");
        _attachmentRate = options.FailureAttachmentsSampleRate;
        _itemLogs = itemLogs;
        _enabled = options.Enabled;
        _sdk = SentrySdk.Init(sentry => {
            sentry.Dsn = options.Dsn;
            sentry.Release = options.Release;
            sentry.Environment = options.Environment;
            sentry.TracesSampleRate = options.Tracing ? options.TracesSampleRate : 0;
            sentry.SendDefaultPii = false;
            sentry.ServerName = string.Empty;
            sentry.AttachStacktrace = false;
            sentry.AutoSessionTracking = false;
            sentry.IsGlobalModeEnabled = true;
            sentry.ShutdownTimeout = FlushTimeout;
            // Fire-and-forget tasks are not the Worker's failures; an exception that ends the process is.
            sentry.DisableUnobservedTaskExceptionCapture();
            sentry.EnableLogs = true;
            _scrubber.Install(sentry, ReportFault, () => _enabled);
            if (transport is not null) {
                sentry.Transport = transport;
            }
        });
        SentrySdk.ConfigureScope(scope => scope.User = new SentryUser { Id = TelemetryIdentity.AnonymousUserId() });
    }

    internal static IRunTelemetry Create(LaunchManifest manifest, Action<string, string, string> log,
        ITransport? transport = null, Func<Guid, IReadOnlyList<WorkerLogEntry>>? itemLogs = null)
    {
        if (manifest.Telemetry is not { } options || SelfTestProcess && transport is null) {
            return Disabled.Instance;
        }
        try {
            var telemetry = new RunTelemetry(manifest, options, log, transport, itemLogs ?? (_ => []));
            log("INFO", "telemetry", $"遥测已初始化：release={options.Release}，enabled={options.Enabled}。");
            return telemetry;
        } catch (Exception exception) {
            log("WARN", "telemetry", $"遥测初始化失败，已关闭：{exception.GetBaseException().Message}");
            return Disabled.Instance;
        }
    }

    public void Register(IReadOnlyList<string> redactionValues) => _scrubber.Add(redactionValues);

    public void SetEnabled(bool enabled)
    {
        lock (_gate) {
            if (_enabled == enabled) {
                return;
            }
            _enabled = enabled;
            // Off drops the current Run: an unfinished Transaction is never sent. On starts with the next Run.
            Forget();
            _runId = null;
            _enabledSinceUtc = DateTime.UtcNow;
        }
    }

    // The host guards this call; a fault here only loses telemetry.
    public void Observe(WorkerSnapshot snapshot)
    {
        // Captured after the lock: scrubbing runs inside the capture, and callbacks must not wait for it.
        var captures = new List<Action>();
        lock (_gate) {
            if (snapshot.StateRevision <= _revision) {
                return;
            }
            _revision = snapshot.StateRevision;
            if (_enabled) {
                ObserveLocked(snapshot, captures);
                if (snapshot.WorkerState == WorkerState.Faulted && _workerState != WorkerState.Faulted) {
                    captures.Add(FaultCapture(snapshot));
                }
            }
            _workerState = snapshot.WorkerState;
        }
        foreach (var capture in captures) {
            capture();
        }
    }

    // Only the messages that decide a failure or a traced node are parsed; most callbacks return at once.
    public void OnTaskerEvent(Guid planItemId, string message, string details, bool? trace)
    {
        if (!_enabled
            || message != MaaMsg.Tasker.Task.Starting && !message.StartsWith("Node.", StringComparison.Ordinal)) {
            return;
        }
        var traced = trace ?? message == MaaMsg.Node.PipelineNode.Failed;
        lock (_gate) {
            if (!_enabled || !_spans.TryGetValue(planItemId, out var span)) {
                return;
            }
            if (!_traces.TryGetValue(planItemId, out var item)) {
                _traces.Add(planItemId, item = new PlanItemTrace());
            }
            if (message == MaaMsg.Node.Action.Failed) {
                item.Stage = "action";
            } else if (message == MaaMsg.Node.NextList.Failed) {
                item.Stage = "recognition";
            }
            if (!traced && message != MaaMsg.Tasker.Task.Starting && message != MaaMsg.Node.PipelineNode.Starting
                && message != MaaMsg.Node.PipelineNode.Failed) {
                return;
            }

            using var document = JsonDocument.Parse(details);
            var root = document.RootElement;
            var taskId = Number(root, "task_id");
            var nodeId = Number(root, "node_id");
            var name = root.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : "unknown";
            if (message == MaaMsg.Tasker.Task.Starting) {
                item.MainTaskId ??= taskId;
                return;
            }
            if (message == MaaMsg.Node.PipelineNode.Starting) {
                item.StartStep(taskId, nodeId);
            }
            long? duration = null;
            if ((message == MaaMsg.Node.PipelineNode.Succeeded || message == MaaMsg.Node.PipelineNode.Failed)
                && item.Steps.Remove(taskId, out var step) && step.Node == nodeId) {
                duration = (long)Stopwatch.GetElapsedTime(step.Started).TotalMilliseconds;
            }
            string? stage = null;
            if (message == MaaMsg.Node.PipelineNode.Failed) {
                stage = item.Stage;
                item.Record(new NodeFailure(name, stage, taskId));
            }
            if (!traced || _nodeSpanBudget <= 0) {
                return;
            }
            _nodeSpanBudget--;
            var node = span.StartChild("node", name);
            node.SetData("message", message);
            node.SetData("task_id", taskId);
            node.SetData("node_id", nodeId);
            if (stage is not null) {
                node.SetData("stage", stage);
            }
            if (duration is not null) {
                node.SetData("duration_ms", duration.Value);
            }
            var failed = message.EndsWith(".Failed", StringComparison.Ordinal);
            node.Finish(failed ? SpanStatus.InternalError : SpanStatus.Ok);
        }
    }

    public void AgentStarted(Guid planItemId, int processId)
    {
        if (!_enabled) {
            return;
        }
        lock (_gate) {
            if (_spans.ContainsKey(planItemId)) {
                if (!_traces.TryGetValue(planItemId, out var item)) {
                    _traces.Add(planItemId, item = new PlanItemTrace());
                }
                item.AgentProcessId = processId;
            }
        }
    }

    public void CaptureUnhandled(Exception exception)
    {
        if (_enabled) {
            SentrySdk.CaptureException(exception, scope => scope.SetTag("exception.source", "worker.unhandled"));
        }
    }

    private void ObserveLocked(WorkerSnapshot snapshot, List<Action> captures)
    {
        var run = snapshot.ActiveRun ?? snapshot.LastRun;
        if (run is null) {
            return;
        }
        if (run.RunId != _runId) {
            Forget();
            _runId = run.RunId;
            if (snapshot.ActiveRun is null || run.StartedAtUtc < _enabledSinceUtc) {
                return;
            }
            _transaction = SentrySdk.StartTransaction("maanop.run", "run");
            _transaction.SetTag("run.id", run.RunId.ToString());
            _transaction.SetData("item_count", run.Items.Count);
            _nodeSpanBudget = MaximumSpans - run.Items.Count;
        }
        if (_transaction is not { } transaction) {
            return;
        }

        foreach (var item in run.Items) {
            if (item.StartedAtUtc is null || _finished.Contains(item.PlanItemId)) {
                continue;
            }
            if (!_spans.TryGetValue(item.PlanItemId, out var span)) {
                span = transaction.StartChild(KindName(item), item.TaskName);
                span.SetData("task", item.TaskName);
                foreach (var (key, value) in SummariseOptions(item.ResolvedOptions)) {
                    span.SetData($"option.{key}", value);
                }
                _spans.Add(item.PlanItemId, span);
            }
            if (!IsTerminal(item.State)) {
                continue;
            }
            if (item.EndedAtUtc is { } ended) {
                span.SetData("duration_ms", (long)(ended - item.StartedAtUtc.Value).TotalMilliseconds);
            }
            if (item.Result is { ValueKind: JsonValueKind.Object } result
                && result.TryGetProperty("exitCode", out var exitCode) && exitCode.TryGetInt32(out var code)) {
                span.SetData("exit_code", code);
            }
            span.SetData("result", item.State.ToString());
            if (item.State == PlanItemState.Failed) {
                captures.Add(FailureCapture(run, item, span));
            }
            span.Finish(Status(item.State));
            _traces.Remove(item.PlanItemId);
            _spans.Remove(item.PlanItemId);
            _finished.Add(item.PlanItemId);
        }

        if (snapshot.ActiveRun is null) {
            transaction.SetData("result", run.State.ToString());
            Finish(transaction, Status(run.State));
        } else if (snapshot.WorkerState == WorkerState.Faulted) {
            // The Run will never end on its own: an unconfirmed stop keeps it until the Child Session is ended.
            foreach (var span in _spans.Values) {
                span.Finish(SpanStatus.Aborted);
            }
            transaction.SetData("result", "faulted");
            Finish(transaction, SpanStatus.Aborted);
        }
    }

    private void Finish(ITransactionTracer transaction, SpanStatus status)
    {
        transaction.Finish(status);
        Forget();
        _ = Task.Run(async () => {
            try {
                await SentrySdk.FlushAsync(FlushTimeout);
            } catch (Exception exception) {
                ReportFault(exception);
            }
        });
    }

    // Drops the current Transaction and Spans without sending them.
    private void Forget()
    {
        _transaction = null;
        _spans.Clear();
        _finished.Clear();
        _traces.Clear();
    }

    // Grouped by the node that ended the task: MaaNOP recovers through on_error, so an earlier failure may not be
    // the cause. A Plan Item that failed without a node failure is grouped by the Worker's reason code.
    private Action FailureCapture(RunSnapshot run, PlanItemSnapshot item, ISpan span)
    {
        var trace = _traces.GetValueOrDefault(item.PlanItemId);
        var (cause, stage) = item.Kind == PlanItemKind.MaaTask && trace?.Root is { } root ? (root.Node, root.Stage)
            : (item.Error?.Code ?? "unknown", item.Kind == PlanItemKind.Pretask ? "pretask" : "worker");
        var sentryEvent = new SentryEvent {
            Message = new SentryMessage { Message = $"MaaNOP task failed: {item.TaskName} at {cause} ({stage})" },
            Level = SentryLevel.Error,
            Fingerprint = ["naruto-task-failure", _project, item.TaskName, cause, stage]
        };
        sentryEvent.SetTag("run.id", run.RunId.ToString());
        sentryEvent.SetTag("task.name", item.TaskName);
        sentryEvent.SetTag("task.kind", KindName(item));
        sentryEvent.SetTag("failure.node", cause);
        sentryEvent.SetTag("failure.stage", stage);
        sentryEvent.SetExtra("failure.code", item.Error?.Code);
        sentryEvent.SetExtra("failure.reason", item.Error?.Message);
        if (trace is not null) {
            sentryEvent.SetExtra("failure.recent", trace.Recent.Select(failure =>
                new { node = failure.Node, stage = failure.Stage, task_id = failure.TaskId }).ToArray());
        }
        var agent = trace?.AgentProcessId;
        // Reading the debug folder takes time; neither the Run nor the next one waits for it.
        return () => {
            var pending = new PendingFailure(sentryEvent, span);
            lock (_pending) {
                _pending.Add(pending);
            }
            _ = Task.Run(() => {
                try {
                    SendFailure(pending, run.RunId, item, agent);
                } catch (Exception exception) {
                    ReportFault(exception);
                } finally {
                    lock (_pending) {
                        _pending.Remove(pending);
                    }
                }
            });
        };
    }

    // The event leaves with the screenshots it was sampled for; its logs follow as Sentry Logs, always.
    private void SendFailure(PendingFailure pending, Guid runId, PlanItemSnapshot item, int? agent)
    {
        var sentryEvent = pending.Event;
        var hint = new SentryHint();
        var logs = new List<(string Source, string Text)>();
        try {
            if (item.Kind == PlanItemKind.MaaTask && item.StartedAtUtc is { } started
                && item.EndedAtUtc is { } ended) {
                int[] processes = agent is { } pid ? [Environment.ProcessId, pid] : [Environment.ProcessId];
                var window = new EvidenceWindow(started, ended, processes);
                var (text, status) = FailureEvidence.FrameworkLog(_debugDirectory, window);
                sentryEvent.SetExtra("logs.status", status);
                logs.Add(("maafw", text));
                if (Random.Shared.NextDouble() < _attachmentRate) {
                    var (zip, count, images) = FailureEvidence.ErrorImages(_debugDirectory, window);
                    sentryEvent.SetExtra("attachment.status", images);
                    sentryEvent.SetExtra("attachment.count", count);
                    if (zip is not null) {
                        hint.AddAttachment(zip, "on_error.zip", AttachmentType.Default, "application/zip");
                    }
                } else {
                    sentryEvent.SetExtra("attachment.status", "not_sampled");
                }
            }
            logs.Add(("worker", string.Join("\n", _itemLogs(item.PlanItemId)
                .Select(entry => $"[{entry.TimestampUtc:O}] [{entry.Level}] [{entry.Source}] {entry.Message}"))));
        } catch (Exception exception) {
            sentryEvent.SetExtra("logs.status", "failed");
            ReportFault(exception);
        }
        if (pending.Capture(hint) is not { } eventId) {
            return;
        }
        foreach (var (source, text) in logs) {
            var chunks = Chunks(_scrubber.Scrub(text)).ToArray();
            for (var index = 0; index < chunks.Length; index++) {
                var chunk = index;
                SentrySdk.Logger.LogInfo(log => {
                    log.SetAttribute("evidence.source", source);
                    log.SetAttribute("evidence.event_id", eventId.ToString());
                    log.SetAttribute("run.id", runId.ToString());
                    log.SetAttribute("evidence.chunk", chunk);
                    log.SetAttribute("evidence.chunks", chunks.Length);
                    log.SetAttribute("plan_item.id", item.PlanItemId.ToString());
                    // Without parameters the SDK sends the text as it is, so the JSON in maafw.log needs no escaping.
                }, chunks[index]);
            }
        }
    }

    // Whole lines where they fit, so a chunk reads on its own.
    private static IEnumerable<string> Chunks(string text)
    {
        var chunk = new System.Text.StringBuilder();
        foreach (var line in text.Split('\n')) {
            for (var start = 0; start < line.Length || start == 0; start += LogChunkCharacters) {
                var part = line.Substring(start, Math.Min(LogChunkCharacters, line.Length - start));
                if (chunk.Length != 0 && chunk.Length + part.Length + 1 > LogChunkCharacters) {
                    yield return chunk.ToString();
                    chunk.Clear();
                }
                chunk.Append(chunk.Length == 0 ? part : "\n" + part);
                if (line.Length == 0) {
                    break;
                }
            }
        }
        if (chunk.ToString().Trim().Length != 0) {
            yield return chunk.ToString();
        }
    }

    // Stop unconfirmed, cleanup failed or the Run loop broke: the Worker needs the Child Session ended.
    private Action FaultCapture(WorkerSnapshot snapshot)
    {
        var code = snapshot.WorkerReason?.Code ?? "unknown";
        var sentryEvent = new SentryEvent {
            Message = new SentryMessage { Message = $"Worker faulted: {code}" },
            Level = SentryLevel.Error,
            Fingerprint = ["worker.faulted", _project, code]
        };
        sentryEvent.SetTag("worker.reason", code);
        if (snapshot.ActiveRun is { } run) {
            sentryEvent.SetTag("run.id", run.RunId.ToString());
        }
        sentryEvent.SetExtra("worker.reason_message", snapshot.WorkerReason?.Message);
        return () => SentrySdk.CaptureEvent(sentryEvent);
    }

    private static string KindName(PlanItemSnapshot item) => item.Kind == PlanItemKind.Pretask ? "pretask" : "task";

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    // A select is its case, a checkbox its cases; an input reports only whether each field is filled, since the
    // Worker cannot tell free text from a number and free text may be private.
    private static IEnumerable<(string Key, string Value)> SummariseOptions(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object) {
            yield break;
        }
        foreach (var option in options.EnumerateObject()) {
            switch (option.Value.ValueKind) {
                case JsonValueKind.String:
                    yield return (option.Name, option.Value.GetString()!);
                    break;
                case JsonValueKind.Array:
                    yield return (option.Name, string.Join(",", option.Value.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString())));
                    break;
                case JsonValueKind.Object:
                    foreach (var input in option.Value.EnumerateObject()) {
                        var filled = input.Value.ValueKind == JsonValueKind.String && input.Value.GetString() != "";
                        yield return ($"{option.Name}.{input.Name}", filled ? "filled" : "empty");
                    }
                    break;
            }
        }
    }

    private static bool IsTerminal(PlanItemState state) =>
        state is PlanItemState.Succeeded or PlanItemState.Failed or PlanItemState.Cancelled;

    private static SpanStatus Status(PlanItemState state) => state switch {
        PlanItemState.Succeeded => SpanStatus.Ok,
        PlanItemState.Cancelled => SpanStatus.Cancelled,
        _ => SpanStatus.InternalError
    };

    private static SpanStatus Status(RunState state) => state switch {
        RunState.Succeeded => SpanStatus.Ok,
        RunState.Cancelled => SpanStatus.Cancelled,
        _ => SpanStatus.InternalError
    };

    private void ReportFault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultLogged, 1) == 0) {
            try {
                _log("WARN", "telemetry", $"遥测记录失败，不影响任务：{exception.GetBaseException().Message}");
            } catch {
                // Telemetry must never fail its caller.
            }
        }
    }

    public void Dispose()
    {
        try {
            // A failure whose evidence is still being read gets a moment; then it leaves without it rather than not
            // at all.
            var deadline = DateTime.UtcNow + FlushTimeout;
            PendingFailure[] pending;
            while (true) {
                lock (_pending) {
                    pending = _pending.ToArray();
                }
                if (pending.Length == 0 || DateTime.UtcNow >= deadline) {
                    break;
                }
                Thread.Sleep(20);
            }
            foreach (var failure in pending) {
                failure.Capture(new SentryHint(), "shutdown");
            }
            _sdk.Dispose();
        } catch (Exception exception) {
            ReportFault(exception);
        }
    }

    private sealed record NodeFailure(string Node, string Stage, long TaskId);

    // A failure event is sent exactly once: with its evidence, or without it when the Worker shuts down first.
    private sealed class PendingFailure(SentryEvent sentryEvent, ISpan span)
    {
        private int _captured;

        internal SentryEvent Event { get; } = sentryEvent;

        internal SentryId? Capture(SentryHint hint, string? evidence = null)
        {
            if (Interlocked.Exchange(ref _captured, 1) != 0) {
                return null;
            }
            if (evidence is not null) {
                Event.SetExtra("logs.status", evidence);
                Event.SetExtra("attachment.status", evidence);
            }
            return SentrySdk.CaptureEvent(Event, hint, scope => scope.Span = span);
        }
    }

    // What the callbacks of one Plan Item have shown so far.
    private sealed class PlanItemTrace
    {
        private readonly Queue<NodeFailure> _recent = new();

        internal long? MainTaskId { get; set; }

        internal int? AgentProcessId { get; set; }

        // The failure that most recently preceded a pipeline step's end: an action, or a next list never recognised.
        internal string Stage { get; set; } = "unknown";

        internal Dictionary<long, (long Node, long Started)> Steps { get; } = [];

        // The last failure of the main task; nested run_task failures and recovered ones are only context.
        internal NodeFailure? Root { get; private set; }

        internal IEnumerable<NodeFailure> Recent => _recent;

        // A new step means the pipeline went on: a failure before it on the main task was recovered (on_error), and
        // the stage of an earlier step no longer applies.
        internal void StartStep(long taskId, long nodeId)
        {
            Steps[taskId] = (nodeId, Stopwatch.GetTimestamp());
            if (taskId == MainTaskId) {
                Root = null;
            }
            Stage = "unknown";
        }

        internal void Record(NodeFailure failure)
        {
            if (MainTaskId is null || failure.TaskId == MainTaskId) {
                Root = failure;
            }
            _recent.Enqueue(failure);
            if (_recent.Count > 10) {
                _recent.Dequeue();
            }
            Stage = "unknown";
        }
    }

    private sealed class Disabled : IRunTelemetry
    {
        internal static readonly Disabled Instance = new();

        public void SetEnabled(bool enabled)
        {
        }

        public void Register(IReadOnlyList<string> redactionValues)
        {
        }

        public void OnTaskerEvent(Guid planItemId, string message, string details, bool? trace)
        {
        }

        public void CaptureUnhandled(Exception exception)
        {
        }

        public void AgentStarted(Guid planItemId, int processId)
        {
        }

        public void Observe(WorkerSnapshot snapshot)
        {
        }

        public void Dispose()
        {
        }
    }
}
