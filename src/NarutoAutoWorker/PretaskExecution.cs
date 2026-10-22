using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

// Runs one already-resolved program before any MaaFramework controller is constructed.
internal sealed class PretaskExecution : IPlanItemExecution
{
    private static readonly TimeSpan ProgramTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly LaunchManifest _manifest;
    private readonly RunPlanItem _item;
    private readonly Action<string, string, string> _log;
    private readonly Action _onRunning;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cleanupTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _stopBegun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopRequested;
    private PretaskProcess? _process;

    internal PretaskExecution(LaunchManifest manifest, RunPlanItem item, Action<string, string, string> log,
        Action onRunning, TimeSpan? timeout = null, TimeSpan? cleanupTimeout = null)
    {
        _manifest = manifest;
        _item = item;
        _log = log;
        _onRunning = onRunning;
        _timeout = timeout ?? ProgramTimeout;
        _cleanupTimeout = cleanupTimeout ?? CleanupTimeout;
    }

    public async Task<RuntimeExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        try {
            var result = await ExecuteCoreAsync(cancellationToken);
            if (result.Outcome != RuntimeExecutionOutcome.Succeeded) {
                _completed.TrySetResult();
            }
            return result;
        } catch {
            _completed.TrySetResult();
            throw;
        }
    }

    private async Task<RuntimeExecutionResult> ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token, _stop.Token);
        RuntimeExecutionResult result;
        try {
            if (IsStopRequested()) {
                await _stopBegun.Task.WaitAsync(cancellationToken);
                return Cancelled();
            }
            _process = new PretaskProcess(_log);
            await _process.StartAsync(_item.Program
                ?? throw new InvalidDataException("Pretask 缺少程序。 "), _manifest.ProjectRoot, linked.Token);
            _onRunning();
            _log("INFO", "pretask.lifecycle", $"前置任务已启动：{_item.TaskLabel}。 ");
            var exitCode = await _process.WaitForExitAsync(linked.Token);
            if (IsStopRequested()) {
                await _stopBegun.Task.WaitAsync(cancellationToken);
                result = Cancelled();
            } else if (exitCode == 0) {
                // Root completion is provisional until the supervisor commits success against run.stop.
                result = new RuntimeExecutionResult(RuntimeExecutionOutcome.Succeeded,
                    ProtocolJson.ToElement(new { exitCode }), null);
            } else {
                result = new RuntimeExecutionResult(RuntimeExecutionOutcome.Failed,
                    ProtocolJson.ToElement(new { exitCode }),
                    new StructuredReason("PretaskFailed", $"前置任务退出码为 {exitCode}。 "));
            }
        } catch (OperationCanceledException) {
            if (IsStopRequested()) {
                await _stopBegun.Task.WaitAsync(cancellationToken);
                result = Cancelled();
            } else {
                result = new RuntimeExecutionResult(RuntimeExecutionOutcome.Failed, null,
                    new StructuredReason("PretaskTimeout", $"前置任务超过 {_timeout.TotalSeconds:0.###} 秒。 "));
            }
        } catch (Exception exception) {
            result = new RuntimeExecutionResult(RuntimeExecutionOutcome.Failed, null,
                new StructuredReason("PretaskExecutionFailed", exception.GetBaseException().Message));
        }

        try {
            if (_process is not null && result.Outcome != RuntimeExecutionOutcome.Succeeded) {
                if (!await _process.ReleaseAsync(_cleanupTimeout)) {
                    return Unconfirmed(result, "本次前置程序及其子进程在清理时限内未确认退出。 ");
                }
                _process = null;
            }
            _log(result.Outcome == RuntimeExecutionOutcome.Failed ? "ERROR" : "INFO", "pretask.lifecycle",
                $"前置任务结束：{_item.TaskLabel}，{result.Outcome}。 ");
            return result;
        } catch (Exception exception) {
            return Unconfirmed(result, exception.GetBaseException().Message);
        }
    }

    // Called only while the supervisor holds its state lock, directly before committing item success.
    internal RuntimeExecutionResult CommitSuccess(RuntimeExecutionResult result)
    {
        try {
            _process!.PreserveChildren();
            _process.Dispose();
            _process = null;
            return result;
        } catch (Exception exception) {
            return Unconfirmed(result, exception.GetBaseException().Message);
        } finally {
            _completed.TrySetResult();
        }
    }

    internal async Task<RuntimeExecutionResult> CancelPendingSuccessAsync(CancellationToken cancellationToken)
    {
        try {
            await _stopBegun.Task.WaitAsync(cancellationToken);
            if (!await _process!.ReleaseAsync(_cleanupTimeout)) {
                return Unconfirmed(Cancelled(), "本次前置程序子进程在清理时限内未确认退出。 ");
            }
            _process = null;
            return Cancelled();
        } catch (Exception exception) {
            return Unconfirmed(Cancelled(), exception.GetBaseException().Message);
        } finally {
            _completed.TrySetResult();
        }
    }

    private RuntimeExecutionResult Unconfirmed(RuntimeExecutionResult result, string message)
    {
        // Keep the live job and process handles on this execution. The supervisor retains it while Faulted.
        return new RuntimeExecutionResult(RuntimeExecutionOutcome.StopTimedOut, result.Result,
            new StructuredReason("PretaskTerminationUnconfirmed", message));
    }

    public void RequestStop()
    {
        lock (_gate) {
            _stopRequested = true;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RequestStop();
        _stopBegun.TrySetResult();
        _stop.Cancel();
        await _completed.Task.WaitAsync(_cleanupTimeout + TimeSpan.FromSeconds(5), cancellationToken);
    }

    private bool IsStopRequested()
    {
        lock (_gate) {
            return _stopRequested;
        }
    }

    private static RuntimeExecutionResult Cancelled() => new(RuntimeExecutionOutcome.Cancelled, null, null);
}
