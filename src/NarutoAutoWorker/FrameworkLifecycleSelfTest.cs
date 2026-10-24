using MaaFramework.Binding;
using NarutoAutoGUI.Protocol;
using static NarutoAutoWorker.NativeTaskCleanupSelfTests;

namespace NarutoAutoWorker;

// Opt-in native regression: hold the controller's final inactive action after the terminal task callback.
internal static class FrameworkLifecycleSelfTest
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<int> RunAsync(string outputRoot)
    {
        try {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var root = Path.GetFullPath(outputRoot);
            if (Directory.Exists(root)) {
                throw new InvalidOperationException("原生生命周期测试要求新的输出目录。");
            }
            WorkerFrameworkOptions.Apply(root, new MaaFrameworkOptions(false, false, false), (_, _, _) => { });
            var resourcePath = Path.Combine(root, "resource");
            Directory.CreateDirectory(Path.Combine(resourcePath, "pipeline"));
            File.WriteAllText(Path.Combine(resourcePath, "pipeline", "test.json"), """
                {
                  "Success": {
                    "recognition": "DirectHit", "action": "DoNothing", "pre_delay": 0, "post_delay": 0
                  },
                  "Failure": {
                    "recognition": "DirectHit", "action": "Click", "target": [1, 1, 1, 1],
                    "pre_delay": 0, "post_delay": 0
                  },
                  "Loop": {
                    "recognition": "DirectHit", "action": "DoNothing", "pre_delay": 0,
                    "post_delay": 100, "next": ["Loop"]
                  }
                }
                """);
            await VerifyCleanupBarrierAsync(resourcePath, "Success", MaaJobStatus.Succeeded);
            await VerifyCleanupBarrierAsync(resourcePath, "Failure", MaaJobStatus.Failed);
            await VerifyCleanupBarrierAsync(resourcePath, "Loop", expected: null);
            await VerifyCleanupBarrierAsync(resourcePath, "Success", MaaJobStatus.Succeeded, stopDuringInactive: true);
            Console.WriteLine($"NATIVE LIFECYCLE TEST PASS: runtime={NativeBindingContext.LibraryVersion}");
            return 0;
        } catch (Exception exception) {
            Console.Error.WriteLine($"NATIVE LIFECYCLE TEST FAIL: {exception}");
            return 1;
        }
    }

    // A null expected status means the case is stopped mid-run; Stop owns its final status.
    private static async Task VerifyCleanupBarrierAsync(
        string resourcePath, string entry, MaaJobStatus? expected, bool stopDuringInactive = false)
    {
        var inactiveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseInactive = new ManualResetEventSlim(false);
        var png = FrameworkDiagnosticsSelfTest.CreateFixturePng();
        var fixture = new FrameworkDiagnosticsSelfTest.FixtureController(png, () =>
        {
            inactiveEntered.TrySetResult();
            return releaseInactive.Wait(TestTimeout);
        });
        using var controller = new MaaCustomController(
            fixture, LinkOption.Start, CheckStatusOption.ThrowIfNotSucceeded);
        using var resource = new MaaResource(CheckStatusOption.ThrowIfNotSucceeded, resourcePath);
        using var tasker = new MaaTasker {
            Controller = controller, Resource = resource, DisposeOptions = DisposeOptions.None
        };
        // Registration is sufficient to exercise LinkStop/Dispose; no Python server or game is needed.
        using var agent = MaaAgentClient.Create(tasker);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new WorkerRuntimeExecution(
            null!, Guid.NewGuid(), null!, 0, (_, _, _) => { }, () => started.TrySetResult());
        tasker.Callback += execution.OnTaskerCallback;
        SetField(execution, "_tasker", tasker);
        SetField(execution, "_resource", resource);
        SetField(execution, "_agentClient", agent);
        GetField<TaskCompletionSource<MaaTasker>>(execution, "_taskerReady").SetResult(tasker);
        var job = tasker.AppendTask(entry);
        var nativeCompletion = Task.Run(job.Wait);
        SetField(execution, "_nativeTaskCompletion", nativeCompletion);
        Task<(bool Success, string? Error)>? cleanup = null;
        Task? stop = null;
        MaaJobStatus? stoppedMainStatus = null;
        try {
            if (expected is null) {
                await started.Task.WaitAsync(TestTimeout);
                stop = execution.StopAsync(CancellationToken.None);
            }
            var status = await execution.TaskCompletion.WaitAsync(TestTimeout);
            await inactiveEntered.Task.WaitAsync(TestTimeout);
            Require(stop is not null || status == expected, $"{entry}: 顶层任务结果不符合预期：{status}。");
            if (stopDuringInactive) {
                stop = execution.StopAsync(CancellationToken.None);
                using var timeout = new CancellationTokenSource(TestTimeout);
                while (tasker.IsRunning || tasker.IsStopping) {
                    await Task.Delay(10, timeout.Token);
                }
                var rejected = controller.Inactive();
                Require(rejected.Id == 0, $"停止期间 Controller 应拒绝新动作，实际 ID={rejected.Id}。");
                await Task.WhenAny(stop, Task.Delay(200));
            }
            if (stop is not null) {
                stoppedMainStatus = await nativeCompletion.WaitAsync(TestTimeout);
                // clear() wakes waiters before erasing statuses, so an early waiter may still read Running.
                Require(stoppedMainStatus is MaaJobStatus.Invalid or MaaJobStatus.Running,
                    $"Stop 后主任务 Wait 应在 inactive 阻塞期间提前返回，实际为 {stoppedMainStatus}。");
                Require(!stop.IsCompleted, "inactive 阻塞时 Stop 不得确认完成。");
            } else {
                Require(!nativeCompletion.IsCompleted, "inactive 阻塞时原生 job 不应结束。");
            }
            cleanup = Cleanup(execution, CancellationToken.None);
            Require(GetField<bool>(execution, "_cleanupStarted") == (stop is null), "清理必须等待 Stop 释放闸门。");
            Require(!cleanup.IsCompleted, "任务回调后、inactive 返回前清理不得完成。");
            Require(!agent.IsInvalid && !tasker.IsInvalid && !resource.IsInvalid,
                "原生 job 尚未结束时清理提前释放了 Agent、Tasker 或 Resource。");
        } finally {
            releaseInactive.Set();
            await nativeCompletion.WaitAsync(TestTimeout);
            if (stop is not null) {
                await stop.WaitAsync(TestTimeout);
            }
            if (cleanup is not null) {
                var result = await cleanup.WaitAsync(TestTimeout);
                Require(result.Success, $"{entry}: 清理失败：{result.Error}");
            }
        }
        Require(agent.IsInvalid && tasker.IsInvalid && resource.IsInvalid, "原生 job 结束后未释放全部执行资源。");
        var detail = stop is null ? "terminal callback precedes native completion"
            : $"Stop confirmed after inactive; main wait {stoppedMainStatus}";
        Console.WriteLine($"NATIVE LIFECYCLE CASE PASS: {entry}; lateStop={stopDuringInactive}; {detail}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) {
            throw new InvalidOperationException(message);
        }
    }
}
