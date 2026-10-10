using System.Diagnostics;
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
        var attempts = 0;
        var skipped = 0;
        try {
            for (var revision = 1; !child.HasExited; revision++) {
                timeout.Token.ThrowIfCancellationRequested();
                Array.Fill(pixels, (byte)(revision % 251));
                attempts++;
                if (!writer.TryPublish(1, revision, DateTime.UtcNow, 640, 360, pixels)) {
                    skipped++;
                }
                await Task.Delay(10, timeout.Token);
            }
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0 || !(await output).Contains("PREVIEW READER PASS", StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"双进程完整帧验证失败（写入 {attempts} 次，跳过 {skipped} 次）：{await error}");
            }
            // Both loops wake on the same timer tick, so a polling reader must not keep the writer from publishing.
            if (skipped * 10 > attempts) {
                throw new InvalidOperationException($"读者轮询挤占了写入：{attempts} 次写入中跳过 {skipped} 次。");
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
                if (pixels.AsSpan().ContainsAnyExcept(expected)) {
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
        var paused = request with { Paused = true };
        service.Handle(ProtocolOperations.PreviewRenew, connection, paused);
        await Task.Delay(100);
        var pausedCaptures = source.Captures;
        var pausedOpens = source.OpenCount;
        await Task.Delay(250);
        if (source.Captures != pausedCaptures || source.DisposedCount != 0) {
            throw new InvalidOperationException("暂停期间必须停采并保留 Controller。");
        }
        service.Handle(ProtocolOperations.PreviewRenew, connection, request);
        await WaitUntilAsync(() => source.Captures > pausedCaptures);
        if (source.OpenCount != pausedOpens) {
            throw new InvalidOperationException("恢复预览不应重建有效 Controller。");
        }
        source.Fail = true;
        await Task.Delay(100);
        if (!reader.TryRead(pixels, out frame) || frame is not { Revision: > 0 }) {
            throw new InvalidOperationException("暂时截图失败未保留旧帧。");
        }
        source.Fail = false;
        service.Handle(ProtocolOperations.PreviewRenew, connection, paused);
        source.Target = null;
        waiting = await WaitForStateAsync(service, connection, paused, PreviewState.WaitingForWindow);
        if (waiting.Generation <= streaming.Generation) {
            throw new InvalidOperationException("窗口关闭未使目标代次失效。");
        }
        await WaitUntilAsync(() => reader.TryRead(pixels, out var cleared) && cleared is { Revision: 0 });
        source.Target = new PreviewTarget(2, 1, 1, 1);
        await WaitForStateAsync(service, connection, paused, PreviewState.WaitingForFrame);
        if (source.OpenCount != pausedOpens) {
            throw new InvalidOperationException("暂停期间为新窗口创建了截图实例。");
        }
        await WaitForStateAsync(service, connection, request, PreviewState.Streaming);
        source.Block = true;
        await WaitUntilAsync(() => source.Entered.IsSet);
        source.Target = null;
        await WaitForStateAsync(service, connection, request, PreviewState.WaitingForWindow);
        if (source.DisposedCount != source.OpenCount - 1) {
            source.Release.Set();
            throw new InvalidOperationException("窗口失效时提前释放了仍在截图的 Controller。");
        }
        source.Target = new PreviewTarget(3, 1, 1, 1);
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
        service.Handle(ProtocolOperations.PreviewRenew, connection, expiring with { Paused = true });
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
        var source = new ScriptedCaptureSource { Target = new PreviewTarget(1, 1, 1, 1) };
        await using var harness = await RunHarness.CreateAsync(previewSource: source);
        var worker = (await harness.SnapshotAsync()).WorkerInstanceId;
        var request = new PreviewRequest(worker, Guid.NewGuid());

        async Task<T> SendAsync<T>(string operation, object data)
        {
            var reply = await harness.RequestAsync(operation, data);
            if (reply.Success != true) {
                throw new InvalidOperationException($"真实 Host 请求失败：{operation} {reply.Error?.Code}");
            }
            return ProtocolJson.Deserialize<T>(reply.Data);
        }

        try {
            var response = await SendAsync<PreviewResponse>(ProtocolOperations.PreviewStart, request);
            while (response.State != PreviewState.Streaming) {
                await Task.Delay(20);
                response = await SendAsync<PreviewResponse>(ProtocolOperations.PreviewRenew, request);
            }
            using var reader = PreviewBuffer.OpenRead(response.Descriptor!);
            if (!reader.TryRead(new byte[PreviewBuffer.MaximumPixelBytes], out var frame)
                || frame is not { Revision: > 0 }) {
                throw new InvalidOperationException("真实 Host Idle 预览未出帧。");
            }
            source.Block = true;
            await WaitUntilAsync(() => source.Entered.IsSet);
            var run = RunSelfTests.Request(RunSelfTests.Plan(1));
            if (await harness.StartAsync(run) != "accepted") {
                throw new InvalidOperationException("截图阻塞时 run.start 未被接受。");
            }
            (await harness.Executions.ExecutingAsync(run.Plan.Items[0].PlanItemId)).ReportRunning();
            var clock = Stopwatch.StartNew();
            var snapshot = await SendAsync<GetSnapshotResponse>(ProtocolOperations.WorkerGetSnapshot, new { });
            await SendAsync<PreviewResponse>(ProtocolOperations.PreviewRenew, request);
            var stop = await SendAsync<RunStopResponse>(ProtocolOperations.RunStop, new RunStopRequest(run.RunId));
            var stopped = await SendAsync<GetSnapshotResponse>(ProtocolOperations.WorkerGetSnapshot, new { });
            if (snapshot.Snapshot.RunState != RunState.Running || stop.Disposition != "stop_requested"
                || stopped.Snapshot.RunState != RunState.Stopping || clock.Elapsed > TimeSpan.FromSeconds(2)
                || source.OpenCount != 1) {
                throw new InvalidOperationException("真实 Host 的控制请求被截图阻塞或重复创建采集。");
            }
            await SendAsync<PreviewResponse>(ProtocolOperations.PreviewStop, request);
        } finally {
            source.Block = false;
            source.Release.Set();
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
