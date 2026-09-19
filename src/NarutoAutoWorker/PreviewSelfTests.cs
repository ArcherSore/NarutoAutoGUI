using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class PreviewSelfTests
{
    internal static void Run()
    {
        VerifyBuffer();
        VerifyAbandonedMutex();
        VerifyTwoProcessesAsync().GetAwaiter().GetResult();
        VerifyServiceAsync().GetAwaiter().GetResult();
        VerifyHostIsolationAsync().GetAwaiter().GetResult();
    }

    private static void VerifyBuffer()
    {
        var identity = new PreviewIdentity(Guid.NewGuid(), 1, Guid.NewGuid());
        using var writer = PreviewBuffer.Create(identity);
        using var reader = PreviewBuffer.OpenRead(writer.Descriptor);
        var pixels = new byte[PreviewBuffer.MaximumPixelBytes];
        Array.Fill(pixels, (byte)37);
        if (!writer.TryPublish(1, 1, DateTime.UtcNow, 640, 360, pixels)) {
            throw new InvalidOperationException("Preview 发布首帧失败。");
        }
        var received = new byte[pixels.Length];
        if (!reader.TryRead(received, out var frame) || frame is not { Revision: 1, Width: 640, Height: 360 }
            || !pixels.AsSpan().SequenceEqual(received)) {
            throw new InvalidOperationException("Preview 映射未传回完整帧。");
        }
        try {
            writer.TryPublish(1, 2, DateTime.UtcNow, 641, 360, pixels);
            throw new InvalidOperationException("Preview 未拒绝越界尺寸。");
        } catch (ArgumentOutOfRangeException) {
        }
        writer.TryClear(2, PreviewState.WaitingForWindow);
        if (!reader.TryRead(received, out frame) || frame is not { Generation: 2, Revision: 0 }) {
            throw new InvalidOperationException("Preview 目标失效后未清空。");
        }
    }

    private static void VerifyAbandonedMutex()
    {
        var identity = new PreviewIdentity(Guid.NewGuid(), 1, Guid.NewGuid());
        using var writer = PreviewBuffer.Create(identity);
        using var reader = PreviewBuffer.OpenRead(writer.Descriptor);
        var name = $@"Global\NarutoAutoGUI.Preview.{identity.WorkerInstanceId:N}.{identity.SubscriptionId:N}";
        using var mutex = Mutex.OpenExisting(name);
        var owner = new Thread(() => mutex.WaitOne());
        owner.Start();
        owner.Join();
        try {
            reader.TryRead(new byte[PreviewBuffer.MaximumPixelBytes], out _);
            throw new InvalidOperationException("未拒绝 abandoned Mutex 对应的帧。");
        } catch (IOException exception) when (exception.Message.Contains("abandoned", StringComparison.Ordinal)) {
        }
    }

    private static async Task VerifyTwoProcessesAsync()
    {
        using var writer = PreviewBuffer.Create(new PreviewIdentity(Guid.NewGuid(),
            (uint)Process.GetCurrentProcess().SessionId, Guid.NewGuid()));
        var start = new ProcessStartInfo(Environment.ProcessPath!) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        start.ArgumentList.Add("--preview-buffer-reader");
        start.ArgumentList.Add(JsonSerializer.Serialize(writer.Descriptor, ProtocolJson.Options));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动映射读者。");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pixels = new byte[PreviewBuffer.MaximumPixelBytes];
        try {
            for (var revision = 1; !child.HasExited; revision++) {
                timeout.Token.ThrowIfCancellationRequested();
                Array.Fill(pixels, (byte)(revision % 251));
                writer.TryPublish(1, revision, DateTime.UtcNow, 640, 360, pixels);
                await Task.Delay(10, timeout.Token);
            }
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0 || !(await output).Contains("PREVIEW READER PASS", StringComparison.Ordinal)) {
                throw new InvalidOperationException($"双进程完整帧验证失败：{await error}");
            }
        } finally {
            if (!child.HasExited) {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }

    internal static int RunBufferReader(string json)
    {
        var descriptor = JsonSerializer.Deserialize<PreviewDescriptor>(json, ProtocolJson.Options)!;
        using var reader = PreviewBuffer.OpenRead(descriptor);
        var pixels = new byte[PreviewBuffer.MaximumPixelBytes];
        var watch = Stopwatch.StartNew();
        long previous = 0;
        var received = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(10) && received < 20) {
            if (reader.TryRead(pixels, out var frame) && frame is { Revision: > 0 } && frame.Revision > previous) {
                var expected = (byte)(frame.Revision % 251);
                if (pixels.Any(pixel => pixel != expected)) {
                    throw new InvalidOperationException("映射读到了不同版本拼接的像素。");
                }
                previous = frame.Revision;
                received++;
            }
            Thread.Sleep(5);
        }
        if (received != 20) {
            throw new TimeoutException("映射读者未取得足够完整帧。");
        }
        Console.WriteLine("PREVIEW READER PASS");
        return 0;
    }

    private static async Task VerifyServiceAsync()
    {
        var worker = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var source = new ScriptedCaptureSource();
        using var service = new WorkerPreviewService(worker, 1, source, (_, _, _) => { });
        var request = new PreviewRequest(worker, Guid.NewGuid());
        service.Handle(ProtocolOperations.PreviewStart, connection, request);
        var waiting = await WaitForStateAsync(service, connection, request, PreviewState.WaitingForWindow);
        if (source.Captures != 0) {
            throw new InvalidOperationException("无窗口时仍在截图。");
        }
        source.Target = new PreviewTarget(1, 1, 1, 1);
        var streaming = await WaitForStateAsync(service, connection, request, PreviewState.Streaming);
        using var reader = PreviewBuffer.OpenRead(streaming.Descriptor!);
        var pixels = new byte[PreviewBuffer.MaximumPixelBytes];
        if (!reader.TryRead(pixels, out var frame) || frame is not { Revision: > 0 }) {
            throw new InvalidOperationException("Idle service 未出帧。");
        }
        source.Fail = true;
        await Task.Delay(100);
        if (!reader.TryRead(pixels, out frame) || frame is not { Revision: > 0 }) {
            throw new InvalidOperationException("暂时截图失败未保留旧帧。");
        }
        source.Fail = false;
        source.Target = null;
        waiting = await WaitForStateAsync(service, connection, request, PreviewState.WaitingForWindow);
        if (waiting.Generation <= streaming.Generation) {
            throw new InvalidOperationException("窗口关闭未使目标代次失效。");
        }
        source.Target = new PreviewTarget(2, 1, 1, 1);
        await WaitForStateAsync(service, connection, request, PreviewState.Streaming);
        source.Block = true;
        await WaitUntilAsync(() => source.Entered.IsSet);
        var clock = Stopwatch.StartNew();
        service.Handle(ProtocolOperations.PreviewStop, connection, request);
        var next = new PreviewRequest(worker, Guid.NewGuid());
        service.Handle(ProtocolOperations.PreviewStart, connection, next);
        service.Handle(ProtocolOperations.PreviewStop, connection, request);
        if (clock.Elapsed > TimeSpan.FromSeconds(1) || source.OpenCount != 2) {
            source.Release.Set();
            throw new InvalidOperationException("截图阻塞影响控制或产生重复采集实例。");
        }
        source.Block = false;
        source.Release.Set();
        await WaitForStateAsync(service, connection, next, PreviewState.Streaming);
        service.Disconnect(connection);
        await WaitUntilAsync(() => source.DisposedCount == source.OpenCount);
        try {
            service.Handle(ProtocolOperations.PreviewRenew, connection, next);
            throw new InvalidOperationException("断线后旧订阅仍能续订。");
        } catch (WorkerRequestException exception) when (exception.Code == "preview_expired") {
        }
        var expiring = new PreviewRequest(worker, Guid.NewGuid());
        service.Handle(ProtocolOperations.PreviewStart, connection, expiring);
        await WaitForStateAsync(service, connection, expiring, PreviewState.Streaming);
        await Task.Delay(TimeSpan.FromSeconds(6.2));
        await WaitUntilAsync(() => source.DisposedCount == source.OpenCount);
        try {
            service.Handle(ProtocolOperations.PreviewRenew, connection, expiring);
            throw new InvalidOperationException("过期租约仍能续订。");
        } catch (WorkerRequestException exception) when (exception.Code == "preview_expired") {
        }
    }

    private static async Task VerifyHostIsolationAsync()
    {
        var worker = Guid.NewGuid();
        var project = new ProjectProvenance("test", "1", 1, "test");
        var manifest = new LaunchManifest(1, worker, "test", "C:\\dummy", project,
            new Win32ControllerDefinition("test", "class", "window", "Cache", "Send", "Send"),
            [], new AgentDefinition("python.exe", [], "C:\\dummy"));
        var source = new ScriptedCaptureSource { Target = new PreviewTarget(1, 1, 1, 1) };
        using var host = new WorkerHost(new WorkerArguments(worker, "test", "C:\\dummy"), manifest, source);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WorkerHost).GetField("_workerState", flags)!.SetValue(host, WorkerState.Ready);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var name = $"NarutoAutoGUI.Preview.HostTest.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connected;
        await using var gui = new ProtocolConnection(server);
        await using var workerConnection = new ProtocolConnection(client);
        var serving = host.ServeConnectionAsync(workerConnection, timeout.Token);
        var request = new PreviewRequest(worker, Guid.NewGuid());
        var responses = System.Threading.Channels.Channel.CreateUnbounded<WireEnvelope>();
        var receiving = Task.Run(async () =>
        {
            while (await gui.ReadAsync(timeout.Token) is { } message) {
                if (message.MessageType == ProtocolMessageTypes.Response) {
                    await responses.Writer.WriteAsync(message, timeout.Token);
                }
            }
        }, timeout.Token);

        async Task<T> SendAsync<T>(string operation, object data)
        {
            var id = Guid.NewGuid();
            await gui.WriteAsync(WireEnvelope.Request(operation, id, data), timeout.Token);
            var reply = await responses.Reader.ReadAsync(timeout.Token);
            if (reply.RequestId != id || reply.Success != true) {
                throw new InvalidOperationException($"真实 Host 请求失败：{operation} {reply.Error?.Code}");
            }
            return ProtocolJson.Deserialize<T>(reply.Data);
        }

        try {
            var response = await SendAsync<PreviewResponse>(ProtocolOperations.PreviewStart, request);
            while (response.State != PreviewState.Streaming) {
                await Task.Delay(20, timeout.Token);
                response = await SendAsync<PreviewResponse>(ProtocolOperations.PreviewRenew, request);
            }
            using var reader = PreviewBuffer.OpenRead(response.Descriptor!);
            if (!reader.TryRead(new byte[PreviewBuffer.MaximumPixelBytes], out var frame)
                || frame is not { Revision: > 0 }) {
                throw new InvalidOperationException("真实 Host Idle 预览未出帧。");
            }
            source.Block = true;
            await WaitUntilAsync(() => source.Entered.IsSet);
            var runId = Guid.NewGuid();
            var empty = ProtocolJson.ToElement(new { });
            var item = new RunPlanItem(Guid.NewGuid(), "test", "test", "test", empty, empty);
            var plan = new RunPlan(1, DateTime.UtcNow, project, "test", empty, [item]);
            var run = new RunSnapshot(runId, "test", RunState.Running, DateTime.UtcNow, DateTime.UtcNow,
                null, null, item.PlanItemId, 0, plan, [], null, null);
            var execution = new WorkerRuntimeExecution(manifest, runId, item, 1, (_, _, _) => { }, () => { });
            typeof(WorkerHost).GetField("_activeRun", flags)!.SetValue(host, run);
            typeof(WorkerHost).GetField("_execution", flags)!.SetValue(host, execution);
            var clock = Stopwatch.StartNew();
            var snapshot = await SendAsync<GetSnapshotResponse>(ProtocolOperations.WorkerGetSnapshot, new { });
            await SendAsync<PreviewResponse>(ProtocolOperations.PreviewRenew, request);
            var stop = await SendAsync<RunStopResponse>(ProtocolOperations.RunStop, new RunStopRequest(runId));
            var stopped = await SendAsync<GetSnapshotResponse>(ProtocolOperations.WorkerGetSnapshot, new { });
            if (snapshot.Snapshot.RunState != RunState.Running || stop.Disposition != "stop_requested"
                || stopped.Snapshot.RunState != RunState.Stopping || clock.Elapsed > TimeSpan.FromSeconds(2)
                || source.OpenCount != 1) {
                throw new InvalidOperationException("真实 Host 的控制请求被截图阻塞或重复创建采集。");
            }
            // Native Tasker setup is outside this seam; finish its controlled readiness failure.
            var ready = (TaskCompletionSource<MaaFramework.Binding.MaaTasker>)typeof(WorkerRuntimeExecution)
                .GetField("_taskerReady", flags)!.GetValue(execution)!;
            ready.TrySetException(new InvalidOperationException("test tasker readiness ends"));
            await SendAsync<PreviewResponse>(ProtocolOperations.PreviewStop, request);
        } finally {
            source.Block = false;
            source.Release.Set();
            timeout.Cancel();
            try {
                await Task.WhenAll(serving, receiving);
            } catch (OperationCanceledException) {
            }
        }
    }

    private static async Task<PreviewResponse> WaitForStateAsync(
        WorkerPreviewService service, Guid connection, PreviewRequest request, PreviewState state)
    {
        PreviewResponse? response = null;
        await WaitUntilAsync(() =>
        {
            response = service.Handle(ProtocolOperations.PreviewRenew, connection, request);
            return response.State == state && response.Descriptor is not null;
        });
        return response!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) {
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) {
                throw new TimeoutException("Preview service 行为验证超时。");
            }
            await Task.Delay(20);
        }
    }

    private sealed class ScriptedCaptureSource : IPreviewCaptureSource
    {
        public volatile PreviewTarget? Target;
        public volatile bool Fail, Block;
        public int OpenCount, DisposedCount, Captures;
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);

        public PreviewTarget? FindTarget() => Target;
        public bool IsValid(PreviewTarget target) => Target == target;
        public IPreviewCapture Open(PreviewTarget target)
        {
            Interlocked.Increment(ref OpenCount);
            return new ScriptedCapture(this);
        }

        private sealed class ScriptedCapture(ScriptedCaptureSource source) : IPreviewCapture
        {
            public PreviewCaptureResult Capture(byte[] pixels)
            {
                Interlocked.Increment(ref source.Captures);
                if (source.Block) {
                    source.Entered.Set();
                    if (!source.Release.Wait(TimeSpan.FromSeconds(8))) {
                        throw new TimeoutException("scripted capture blocked");
                    }
                }
                if (source.Fail) {
                    throw new IOException("scripted failure");
                }
                Array.Clear(pixels);
                return new PreviewCaptureResult(4, 3, DateTime.UtcNow);
            }

            public void Dispose() => Interlocked.Increment(ref source.DisposedCount);
        }
    }
}
