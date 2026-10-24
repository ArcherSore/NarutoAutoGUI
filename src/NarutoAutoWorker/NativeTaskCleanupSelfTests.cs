using System.Reflection;
using MaaFramework.Binding;

namespace NarutoAutoWorker;

internal static class NativeTaskCleanupSelfTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync()
    {
        // Cleanup always joins the main waiter. A confirmed Stop accepts its early Invalid/Running result.
        foreach (var (status, stopConfirmed) in new[] {
            (MaaJobStatus.Succeeded, false), (MaaJobStatus.Failed, false),
            (MaaJobStatus.Invalid, true), (MaaJobStatus.Running, true)
        }) {
            var completion = new TaskCompletionSource<MaaJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var execution = Create(completion.Task);
            if (stopConfirmed) {
                GetField<TaskCompletionSource<bool>>(execution, "_stopConfirmed").SetResult(true);
            }
            var cleanup = Cleanup(execution, CancellationToken.None);
            if (cleanup.IsCompleted) {
                throw new InvalidOperationException("原生任务 waiter 返回前不得完成清理。");
            }
            completion.SetResult(status);
            var result = await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            if (!result.Success) {
                throw new InvalidOperationException($"原生任务 waiter 返回后应可清理：{status}, {result.Error}");
            }
        }

        // Cancellation cancels the managed wait, not the native job. Its resources must remain owned.
        var pending = new TaskCompletionSource<MaaJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledExecution = Create(pending.Task);
        using var cancellation = new CancellationTokenSource();
        var cancelledCleanup = Cleanup(cancelledExecution, cancellation.Token);
        cancellation.Cancel();
        await VerifyPreservedAsync(cancelledExecution, cancelledCleanup);
        pending.SetResult(MaaJobStatus.Succeeded);
        if ((await Cleanup(cancelledExecution, CancellationToken.None)).Success) {
            throw new InvalidOperationException("收尾未确认后不得通过重复清理丢弃保留的上下文。");
        }

        // Without a confirmed Stop, a failed or non-terminal wait leaves native teardown unconfirmed.
        foreach (var completion in new[] {
            Task.FromException<MaaJobStatus>(new InvalidOperationException("scripted native wait failure")),
            Task.FromResult(MaaJobStatus.Running),
            Task.FromResult(MaaJobStatus.Invalid)
        }) {
            var execution = Create(completion);
            await VerifyPreservedAsync(execution, Cleanup(execution, CancellationToken.None));
        }
    }

    private static WorkerRuntimeExecution Create(Task<MaaJobStatus> nativeCompletion)
    {
        var execution = new WorkerRuntimeExecution(null!, Guid.NewGuid(), null!, 0, (_, _, _) => { }, () => { });
        SetField(execution, "_nativeTaskCompletion", nativeCompletion);
        return execution;
    }

    internal static T GetField<T>(WorkerRuntimeExecution execution, string name)
    {
        return (T)typeof(WorkerRuntimeExecution).GetField(name, PrivateInstance)!.GetValue(execution)!;
    }

    internal static void SetField(WorkerRuntimeExecution execution, string name, object value)
    {
        typeof(WorkerRuntimeExecution).GetField(name, PrivateInstance)!.SetValue(execution, value);
    }

    internal static Task<(bool Success, string? Error)> Cleanup(
        WorkerRuntimeExecution execution, CancellationToken cancellationToken)
    {
        return (Task<(bool Success, string? Error)>)typeof(WorkerRuntimeExecution)
            .GetMethod("CleanupAsync", PrivateInstance)!.Invoke(execution, [cancellationToken])!;
    }

    private static async Task VerifyPreservedAsync(
        WorkerRuntimeExecution execution, Task<(bool Success, string? Error)> cleanup)
    {
        var result = await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        var executionResult = await execution.ExecuteAsync(CancellationToken.None);
        if (result.Success || executionResult.Outcome != RuntimeExecutionOutcome.StopTimedOut
            || executionResult.Error?.Code != "NativeTaskCompletionUnconfirmed") {
            throw new InvalidOperationException("原生任务收尾未确认时必须保留上下文，不能报告普通终态。");
        }
    }
}
