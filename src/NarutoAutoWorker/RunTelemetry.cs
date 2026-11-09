using System.Text.Json;
using NarutoAutoGUI.Protocol;
using Sentry;
using Sentry.Extensibility;

namespace NarutoAutoWorker;

// The Worker's side of telemetry (ADR 0009). Every member is a side path: it never throws to its caller, never
// blocks a Run, and a broken SDK only turns telemetry off.
internal interface IRunTelemetry : IDisposable
{
    void SetEnabled(bool enabled);

    // Called with every published Worker snapshot, in no particular thread.
    void Observe(WorkerSnapshot snapshot);
}

internal sealed class RunTelemetry : IRunTelemetry
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly Action<string, string, string> _log;
    private readonly IDisposable _sdk;
    private readonly Dictionary<Guid, ISpan> _spans = [];
    private readonly HashSet<Guid> _finished = [];
    private bool _enabled;
    // Only a Run that started while telemetry was on is reported whole.
    private DateTime _enabledSinceUtc = DateTime.MinValue;
    private long _revision;
    private Guid? _runId;
    private ITransactionTracer? _transaction;
    private int _faultLogged;

    internal static IRunTelemetry None => Disabled.Instance;

    // A self-test may only send to the transport it supplies.
    internal static bool SelfTestProcess { get; set; }

    private RunTelemetry(TelemetryOptions options, Action<string, string, string> log, ITransport? transport)
    {
        _log = log;
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
            sentry.DisableAppDomainUnhandledExceptionCapture();
            sentry.DisableUnobservedTaskExceptionCapture();
            if (transport is not null) {
                sentry.Transport = transport;
            }
        });
        SentrySdk.ConfigureScope(scope => scope.User = new SentryUser { Id = TelemetryIdentity.AnonymousUserId() });
    }

    internal static IRunTelemetry Create(LaunchManifest manifest, Action<string, string, string> log,
        ITransport? transport = null)
    {
        if (manifest.Telemetry is not { } options || SelfTestProcess && transport is null) {
            return Disabled.Instance;
        }
        try {
            var telemetry = new RunTelemetry(options, log, transport);
            log("INFO", "telemetry", $"遥测已初始化：release={options.Release}，enabled={options.Enabled}。");
            return telemetry;
        } catch (Exception exception) {
            log("WARN", "telemetry", $"遥测初始化失败，已关闭：{exception.GetBaseException().Message}");
            return Disabled.Instance;
        }
    }

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
        lock (_gate) {
            if (snapshot.StateRevision <= _revision) {
                return;
            }
            _revision = snapshot.StateRevision;
            if (_enabled) {
                ObserveLocked(snapshot);
            }
        }
    }

    private void ObserveLocked(WorkerSnapshot snapshot)
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
        }
        if (_transaction is not { } transaction) {
            return;
        }

        foreach (var item in run.Items) {
            if (item.StartedAtUtc is null || _finished.Contains(item.PlanItemId)) {
                continue;
            }
            if (!_spans.TryGetValue(item.PlanItemId, out var span)) {
                span = transaction.StartChild(item.Kind == PlanItemKind.Pretask ? "pretask" : "task", item.TaskName);
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
            span.Finish(Status(item.State));
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
    }

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
            _sdk.Dispose();
        } catch (Exception exception) {
            ReportFault(exception);
        }
    }

    private sealed class Disabled : IRunTelemetry
    {
        internal static readonly Disabled Instance = new();

        public void SetEnabled(bool enabled)
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
