using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

// Serves a real WorkerHost over an in-process pipe; Plan Items run through scripted executions.
internal sealed class RunHarness : IAsyncDisposable
{
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    internal static readonly string ProfileDigest = "sha256:" + new string('a', 64);
    internal static readonly ProjectProvenance Project = new("test", "1", 1, "test");

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<WireEnvelope> _responses = Channel.CreateUnbounded<WireEnvelope>();
    private readonly object _gate = new();
    private readonly List<WorkerSnapshot> _states = [];
    private readonly List<WorkerLogEntry> _logs = [];
    private TaskCompletionSource _eventArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream _server = null!;
    private ProtocolConnection _gui = null!;
    private ProtocolConnection _worker = null!;
    private Task _receiving = Task.CompletedTask;

    private RunHarness(Guid workerId, IPreviewCaptureSource? previewSource)
    {
        Executions = new ScriptedExecutions();
        var manifest = new LaunchManifest(ProtocolConstants.LaunchContextVersion, workerId, ProfileDigest,
            "C:\\dummy", Project, new Win32ControllerDefinition("test", "class", "window", "Cache", "Send", "Send"),
            [], new AgentDefinition("python.exe", [], "C:\\dummy"));
        Host = new WorkerHost(new WorkerArguments(workerId, "test", "C:\\dummy"), manifest, previewSource ?? new NoWindowSource(),
            Executions.Create, _ => Task.FromResult((Dependencies, ReadinessFailure)));
        Executions.Probe = Host.GetSnapshot;
    }

    internal WorkerHost Host { get; }

    internal ScriptedExecutions Executions { get; }

    internal RecordingStream Wire { get; private set; } = null!;

    internal Task Serving { get; private set; } = Task.CompletedTask;

    internal StructuredReason? ReadinessFailure { get; set; }

    private static DependencyStatus Dependencies
    {
        get
        {
            var ok = new DependencyCheck(true, "test", null);
            return new DependencyStatus(DateTime.UtcNow, "test", "test", ok, ok, ok, ok, ok);
        }
    }

    internal static async Task<RunHarness> CreateAsync(
        bool initialize = true, IPreviewCaptureSource? previewSource = null)
    {
        var harness = new RunHarness(Guid.NewGuid(), previewSource);
        await harness.ConnectAsync();
        if (initialize) {
            await harness.Host.InitializeAsync(CancellationToken.None);
        }
        return harness;
    }

    private async Task ConnectAsync()
    {
        var name = $"NarutoAutoGUI.Run.SelfTest.{Guid.NewGuid():N}";
        _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = _server.WaitForConnectionAsync(_shutdown.Token);
        await client.ConnectAsync(_shutdown.Token);
        await connected;
        Wire = new RecordingStream(client);
        Executions.Wire = Wire;
        _gui = new ProtocolConnection(_server);
        _worker = new ProtocolConnection(Wire);
        Serving = Host.ServeConnectionAsync(_worker, _shutdown.Token);
        _receiving = Task.Run(ReceiveAsync);
    }

    private async Task ReceiveAsync()
    {
        try {
            while (await _gui.ReadAsync(_shutdown.Token) is { } message) {
                if (message.MessageType == ProtocolMessageTypes.Response) {
                    await _responses.Writer.WriteAsync(message, _shutdown.Token);
                } else {
                    TaskCompletionSource arrived;
                    lock (_gate) {
                        if (message.Operation == ProtocolOperations.LogEntry) {
                            _logs.Add(ProtocolJson.Deserialize<LogEntryEvent>(message.Data).Entry);
                        } else {
                            _states.Add(ProtocolJson.Deserialize<StateChangedEvent>(message.Data).Snapshot);
                        }
                        arrived = _eventArrived;
                        _eventArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    arrived.TrySetResult();
                }
            }
        } catch (Exception) {
            // The connection ends with the test; requests still waiting then fail on the completed channel.
        } finally {
            _responses.Writer.TryComplete();
        }
    }

    internal async Task<WireEnvelope> RequestAsync<T>(string operation, T data)
    {
        var requestId = Guid.NewGuid();
        await _gui.WriteAsync(WireEnvelope.Request(operation, requestId, data), _shutdown.Token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        limit.CancelAfter(WaitLimit);
        var response = await _responses.Reader.ReadAsync(limit.Token);
        RunSelfTests.Expect(response.RequestId == requestId, $"{operation} 响应与请求不匹配。");
        return response;
    }

    internal async Task<string> StartAsync(RunStartRequest request) =>
        Outcome<RunStartResponse>(await RequestAsync(ProtocolOperations.RunStart, request), r => r.Disposition);

    internal async Task<string> StopAsync(Guid runId) =>
        Outcome<RunStopResponse>(
            await RequestAsync(ProtocolOperations.RunStop, new RunStopRequest(runId)), r => r.Disposition);

    internal async Task<WorkerSnapshot> SnapshotAsync()
    {
        var response = await RequestAsync(ProtocolOperations.WorkerGetSnapshot, new { });
        RunSelfTests.Expect(response.Success == true, "worker.getSnapshot 失败。");
        return ProtocolJson.Deserialize<GetSnapshotResponse>(response.Data).Snapshot;
    }

    // Responses carry either the disposition or the error code, so one string covers both outcomes.
    private static string Outcome<T>(WireEnvelope response, Func<T, string> disposition) =>
        response.Success == true ? disposition(ProtocolJson.Deserialize<T>(response.Data))
            : "error:" + response.Error?.Code;

    // A later state may replace an earlier one in the event channel, so callers wait for states that stay true.
    internal Task<WorkerSnapshot> WaitForStateAsync(Func<WorkerSnapshot, bool> condition, string description) =>
        WaitForEventAsync(() => _states.LastOrDefault(condition), description);

    internal Task<WorkerLogEntry> WaitForLogAsync(Func<WorkerLogEntry, bool> condition, string description) =>
        WaitForEventAsync(() => _logs.FirstOrDefault(condition), description);

    private async Task<T> WaitForEventAsync<T>(Func<T?> find, string description) where T : class
    {
        using var limit = new CancellationTokenSource(WaitLimit);
        while (true) {
            Task arrived;
            lock (_gate) {
                if (find() is { } found) {
                    return found;
                }
                arrived = _eventArrived.Task;
            }
            try {
                await arrived.WaitAsync(limit.Token);
            } catch (OperationCanceledException) {
                throw new TimeoutException($"等待状态超时：{description}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Executions.ReleaseAll();
        _shutdown.Cancel();
        try {
            await Task.WhenAll(Serving, _receiving).WaitAsync(WaitLimit);
        } catch (Exception) {
            // Tests that expect a particular connection outcome await Serving themselves.
        }
        await _gui.DisposeAsync();
        await _worker.DisposeAsync();
        Host.Dispose();
        _shutdown.Dispose();
    }

    private sealed class NoWindowSource : IPreviewCaptureSource
    {
        public PreviewTarget? FindTarget() => null;
        public bool IsValid(PreviewTarget target) => false;
        public IPreviewCapture Open(PreviewTarget target) => throw new InvalidOperationException("没有预览窗口。");
    }
}

internal enum ScriptedStop { Complete, Throw, Block }

// Records every execution call. Each call probes the Worker state lock from another thread: Monitor is reentrant,
// so only a second thread can show that the caller does not hold the lock.
internal sealed class ScriptedExecutions
{
    internal static readonly TimeSpan LockProbeLimit = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly List<ScriptedExecution> _created = [];
    private readonly List<string> _calls = [];
    private readonly List<string> _violations = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Func<WorkerSnapshot> Probe { get; set; } = null!;

    internal RecordingStream Wire { get; set; } = null!;

    internal ScriptedStop StopBehavior { get; set; }

    internal IReadOnlyList<string> Violations
    {
        get
        {
            lock (_gate) {
                return _violations.ToArray();
            }
        }
    }

    internal IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate) {
                return _calls.ToArray();
            }
        }
    }

    internal IReadOnlyList<ScriptedExecution> Created
    {
        get
        {
            lock (_gate) {
                return _created.ToArray();
            }
        }
    }

    internal IPlanItemExecution Create(Guid runId, RunPlanItem item, Action onRunning)
    {
        var execution = new ScriptedExecution(this, item, onRunning);
        lock (_gate) {
            _created.Add(execution);
        }
        return execution;
    }

    // Returns once ExecuteAsync has been called for the Plan Item, which happens only after its commit.
    internal async Task<ScriptedExecution> ExecutingAsync(Guid planItemId)
    {
        using var limit = new CancellationTokenSource(RunHarness.WaitLimit);
        while (true) {
            Task changed;
            lock (_gate) {
                if (_created.FirstOrDefault(execution => execution.Item.PlanItemId == planItemId
                                                         && execution.ExecuteCalled) is { } found) {
                    return found;
                }
                changed = _changed.Task;
            }
            try {
                await changed.WaitAsync(limit.Token);
            } catch (OperationCanceledException) {
                throw new TimeoutException($"Plan Item {planItemId} 未开始执行。");
            }
        }
    }

    // observe sees the probed snapshot (null when the probe was blocked) before waiters are released.
    internal void Record(ScriptedExecution execution, string member, Action<WorkerSnapshot?>? observe = null)
    {
        var probe = Task.Run(Probe);
        var released = probe.Wait(LockProbeLimit);
        TaskCompletionSource changed;
        lock (_gate) {
            observe?.Invoke(released ? probe.Result : null);
            _calls.Add($"{member}:{execution.Item.TaskName}");
            if (!released) {
                _violations.Add($"{member}:{execution.Item.TaskName}");
            }
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    internal void ReleaseAll()
    {
        foreach (var execution in Created) {
            execution.Release();
        }
    }
}

internal sealed class ScriptedExecution : IPlanItemExecution
{
    private readonly ScriptedExecutions _owner;
    private readonly Action _onRunning;
    private readonly TaskCompletionSource<RuntimeExecutionResult> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestStops;
    private int _stops;
    private volatile bool _executeCalled;

    internal ScriptedExecution(ScriptedExecutions owner, RunPlanItem item, Action onRunning)
    {
        _owner = owner;
        Item = item;
        _onRunning = onRunning;
    }

    internal RunPlanItem Item { get; }

    internal bool ExecuteCalled => _executeCalled;

    internal int RequestStops => Volatile.Read(ref _requestStops);

    internal int Stops => Volatile.Read(ref _stops);

    internal WorkerSnapshot? SnapshotAtExecute { get; private set; }

    // Both keep what had been written at the first call, which is the one the ordering contract is about.
    internal IReadOnlyList<WireEnvelope>? WrittenAtRequestStop { get; private set; }

    internal IReadOnlyList<WireEnvelope>? WrittenAtStop { get; private set; }

    public Task<RuntimeExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        _owner.Record(this, "ExecuteAsync", snapshot =>
        {
            SnapshotAtExecute = snapshot;
            _executeCalled = true;
        });
        return _result.Task;
    }

    public void RequestStop()
    {
        WrittenAtRequestStop ??= _owner.Wire.Frames();
        Interlocked.Increment(ref _requestStops);
        _owner.Record(this, "RequestStop");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        WrittenAtStop ??= _owner.Wire.Frames();
        Interlocked.Increment(ref _stops);
        _owner.Record(this, "StopAsync");
        _stopCalled.TrySetResult();
        switch (_owner.StopBehavior) {
            case ScriptedStop.Throw:
                throw new StopConfirmationException("scripted stop confirmation failure");
            case ScriptedStop.Block:
                await _stopReleased.Task;
                break;
        }
    }

    internal async Task StopCalledAsync()
    {
        try {
            await _stopCalled.Task.WaitAsync(RunHarness.WaitLimit);
        } catch (TimeoutException) {
            throw new TimeoutException($"{Item.TaskName} 未调用 StopAsync。");
        }
    }

    internal void Complete(RuntimeExecutionOutcome outcome, string? errorCode = null) =>
        _result.TrySetResult(new RuntimeExecutionResult(outcome,
            ProtocolJson.ToElement(new { scripted = outcome.ToString(), item = Item.TaskName }),
            errorCode is null ? null : new StructuredReason(errorCode, $"scripted {errorCode}")));

    // The MaaFramework callback thread reports Running; a dedicated thread stands in for it.
    internal void ReportRunning()
    {
        var thread = new Thread(() => _onRunning()) { IsBackground = true, Name = "scripted-maa-callback" };
        thread.Start();
        RunSelfTests.Expect(thread.Join(RunHarness.WaitLimit), $"{Item.TaskName} 的 onRunning 未在时限内返回。");
    }

    internal void Release()
    {
        _stopReleased.TrySetResult();
        _result.TrySetResult(new RuntimeExecutionResult(RuntimeExecutionOutcome.Failed, null,
            new StructuredReason("SelfTestEnded", "self-test released the execution")));
    }
}

// Records what the Worker has completely written, so ordering checks need no reads on the GUI side.
internal sealed class RecordingStream(Stream inner) : Stream
{
    private readonly object _gate = new();
    private readonly MemoryStream _written = new();

    internal Func<IReadOnlyList<WireEnvelope>, bool>? FailWhen { get; set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal IReadOnlyList<WireEnvelope> Frames()
    {
        byte[] bytes;
        lock (_gate) {
            bytes = _written.ToArray();
        }
        var frames = new List<WireEnvelope>();
        var offset = 0;
        while (bytes.Length - offset >= sizeof(uint)) {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            if (bytes.Length - offset - sizeof(uint) < length) {
                break;
            }
            frames.Add(JsonSerializer.Deserialize<WireEnvelope>(
                bytes.AsSpan(offset + sizeof(uint), length), ProtocolJson.Options)!);
            offset += sizeof(uint) + length;
        }
        return frames;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (FailWhen?.Invoke(Frames()) == true) {
            throw new IOException("scripted write failure");
        }
        await inner.WriteAsync(buffer, cancellationToken);
        lock (_gate) {
            _written.Write(buffer.Span);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
