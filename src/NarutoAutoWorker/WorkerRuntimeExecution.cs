using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaaFramework.Binding;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal enum RuntimeExecutionOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    StopTimedOut,
    CleanupFailed
}

internal sealed record RuntimeExecutionResult(
    RuntimeExecutionOutcome Outcome,
    JsonElement? Result,
    StructuredReason? Error,
    bool ForcedAgentTermination);

internal sealed class WorkerRuntimeExecution
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AgentExitGracePeriod = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly LaunchManifest _manifest;
    private readonly Guid _runId;
    private readonly RunPlanItem _item;
    private readonly uint _childSessionId;
    private readonly Action<string, string, string> _log;
    private readonly Action _onRunning;
    private readonly TaskCompletionSource<MaaTasker> _taskerReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopConfirmed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private MaaWin32Controller? _controller;
    private MaaResource? _resource;
    private MaaTasker? _tasker;
    private MaaAgentClient? _agentClient;
    private Process? _agentProcess;
    private bool _stopRequested;
    private bool _runningReported;
    private bool _preserveContext;

    internal WorkerRuntimeExecution(
        LaunchManifest manifest,
        Guid runId,
        RunPlanItem item,
        uint childSessionId,
        Action<string, string, string> log,
        Action onRunning)
    {
        _manifest = manifest;
        _runId = runId;
        _item = item;
        _childSessionId = childSessionId;
        _log = log;
        _onRunning = onRunning;
    }

    internal async Task<RuntimeExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            var window = FindTargetWindow();
            _log("INFO", "runtime.window", $"目标窗口 HWND=0x{window.Handle.ToInt64():X}，Name={window.Name}。 ");
            _controller = new MaaWin32Controller(
                window.Handle,
                ParseEnum<Win32ScreencapMethod>(_manifest.Controller.ScreencapMethod),
                ParseEnum<Win32InputMethod>(_manifest.Controller.MouseMethod),
                ParseEnum<Win32InputMethod>(_manifest.Controller.KeyboardMethod),
                LinkOption.Start,
                CheckStatusOption.ThrowIfNotSucceeded);
            _resource = new MaaResource(
                CheckStatusOption.ThrowIfNotSucceeded,
                _manifest.Resources.SelectMany(resource => resource.Paths));
            _tasker = new MaaTasker
            {
                Controller = _controller,
                Resource = _resource,
                DisposeOptions = DisposeOptions.None
            };
            if (!_tasker.IsInitialized)
            {
                throw new InvalidOperationException("MaaTasker 初始化后 IsInitialized=false。 ");
            }
            _taskerReady.TrySetResult(_tasker);

            _agentClient = MaaAgentClient.Create(_tasker);
            var linked = _agentClient.LinkStart(StartAgentProcess, cancellationToken);
            if (!linked || !_agentClient.IsConnected)
            {
                throw new InvalidOperationException("MaaAgentClient LinkStart 未建立连接。 ");
            }
            _log("INFO", "runtime.agent", $"Python Agent 已连接，identifier={_agentClient.Id}。 ");

            var job = _tasker.AppendTask(_item.Entry, _item.PipelineOverride.GetRawText());
            _log("INFO", "runtime.task", $"已提交 MaaFramework task：{_item.Entry}，jobId={job.Id}。 ");
            MaaJobStatus status;
            while (!(status = job.Status).IsDone())
            {
                if (status.IsRunning())
                {
                    ReportRunningOnce();
                }
                await Task.Delay(100, cancellationToken);
            }

            if (_stopRequested)
            {
                try
                {
                    await _stopConfirmed.Task.WaitAsync(StopTimeout, cancellationToken);
                }
                catch (Exception exception) when (exception is TimeoutException or StopConfirmationException)
                {
                    _preserveContext = true;
                    return new RuntimeExecutionResult(
                        RuntimeExecutionOutcome.StopTimedOut,
                        null,
                        new StructuredReason("StopTimeout", exception.GetBaseException().Message),
                        false);
                }

                var cleanup = await CleanupAsync(cancellationToken);
                return cleanup.Success
                    ? new RuntimeExecutionResult(
                        RuntimeExecutionOutcome.Cancelled,
                        ProtocolJson.ToElement(new { maaJobStatus = status.ToString() }),
                        null,
                        cleanup.ForcedAgentTermination)
                    : new RuntimeExecutionResult(
                        RuntimeExecutionOutcome.CleanupFailed,
                        ProtocolJson.ToElement(new { maaJobStatus = status.ToString() }),
                        new StructuredReason("AgentCleanupFailed", cleanup.Error!),
                        cleanup.ForcedAgentTermination);
            }

            var normalCleanup = await CleanupAsync(cancellationToken);
            if (!normalCleanup.Success)
            {
                return new RuntimeExecutionResult(
                    RuntimeExecutionOutcome.CleanupFailed,
                    ProtocolJson.ToElement(new { maaJobStatus = status.ToString() }),
                    new StructuredReason("AgentCleanupFailed", normalCleanup.Error!),
                    normalCleanup.ForcedAgentTermination);
            }
            if (status.IsSucceeded())
            {
                return new RuntimeExecutionResult(
                    RuntimeExecutionOutcome.Succeeded,
                    ProtocolJson.ToElement(new { maaJobStatus = status.ToString() }),
                    null,
                    normalCleanup.ForcedAgentTermination);
            }
            return new RuntimeExecutionResult(
                RuntimeExecutionOutcome.Failed,
                ProtocolJson.ToElement(new { maaJobStatus = status.ToString() }),
                new StructuredReason("MaaTaskFailed", $"MaaFramework task 终态为 {status}。 "),
                normalCleanup.ForcedAgentTermination);
        }
        catch (Exception exception)
        {
            _taskerReady.TrySetException(exception);
            if (_preserveContext)
            {
                return new RuntimeExecutionResult(
                    RuntimeExecutionOutcome.StopTimedOut,
                    null,
                    new StructuredReason("StopTimeout", exception.GetBaseException().Message),
                    false);
            }

            var cleanup = await CleanupAsync(CancellationToken.None);
            var reason = cleanup.Success
                ? new StructuredReason("RunExecutionFailed", exception.GetBaseException().Message)
                : new StructuredReason(
                    "AgentCleanupFailed",
                    $"{exception.GetBaseException().Message}；清理失败：{cleanup.Error}");
            return new RuntimeExecutionResult(
                cleanup.Success ? RuntimeExecutionOutcome.Failed : RuntimeExecutionOutcome.CleanupFailed,
                null,
                reason,
                cleanup.ForcedAgentTermination);
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        RequestStop();

        try
        {
            var tasker = await _taskerReady.Task.WaitAsync(StopTimeout, cancellationToken);
            var stopJob = tasker.Stop();
            var status = await Task.Run(stopJob.Wait, cancellationToken)
                .WaitAsync(StopTimeout, cancellationToken);
            var deadline = DateTime.UtcNow + StopTimeout;
            while ((tasker.IsRunning || tasker.IsStopping) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
            if (!status.IsSucceeded() || tasker.IsRunning || tasker.IsStopping)
            {
                throw new StopConfirmationException(
                    $"MaaFramework Stop 未确认：job={status}，running={tasker.IsRunning}，stopping={tasker.IsStopping}。 ");
            }
            _stopConfirmed.TrySetResult(true);
            _log("INFO", "runtime.stop", "MaaFramework Stop 已确认。 ");
        }
        catch (Exception exception)
        {
            var actual = exception is StopConfirmationException
                ? exception
                : new StopConfirmationException(
                    $"MaaFramework Stop 在 {StopTimeout.TotalSeconds:0} 秒内未确认。",
                    exception);
            _stopConfirmed.TrySetException(actual);
            throw actual;
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
        }
    }

    private DesktopWindowInfo FindTargetWindow()
    {
        var classRegex = new Regex(
            _manifest.Controller.ClassRegex,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var windowRegex = new Regex(
            _manifest.Controller.WindowRegex,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        using var windows = MaaToolkit.Shared.Desktop.Window.Find();
        foreach (var window in windows)
        {
            if (!classRegex.IsMatch(window.ClassName) || !windowRegex.IsMatch(window.Name))
            {
                continue;
            }
            _ = GetWindowThreadProcessId(window.Handle, out var processId);
            if (processId == 0)
            {
                continue;
            }
            using var process = Process.GetProcessById(checked((int)processId));
            if (process.SessionId != _childSessionId)
            {
                continue;
            }
            _log(
                "INFO",
                "runtime.window",
                $"目标窗口进程 PID={processId}，SessionId={process.SessionId}，Path={TryGetProcessPath(process)}。 ");
            return window;
        }
        throw new InvalidOperationException(
            $"未在 Child Session {_childSessionId} 找到窗口：class={_manifest.Controller.ClassRegex}，name={_manifest.Controller.WindowRegex}。 ");
    }

    private Process StartAgentProcess(string identifier, string nativeAssemblyDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _manifest.Agent.ChildExec,
            WorkingDirectory = _manifest.Agent.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in _manifest.Agent.ChildArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add(identifier);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                _log("INFO", "agent.stdout", args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                _log("WARN", "agent.stderr", args.Data);
            }
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动 Python Agent。 ");
        }
        _agentProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _log("INFO", "runtime.agent", $"已启动 Python Agent PID={process.Id}。 ");
        return process;
    }

    private async Task<(bool Success, bool ForcedAgentTermination, string? Error)> CleanupAsync(
        CancellationToken cancellationToken)
    {
        if (_preserveContext)
        {
            return (false, false, "Stop 未确认，保留 execution context 供诊断。 ");
        }

        var forced = false;
        var errors = new List<string>();
        try
        {
            if (_agentClient is not null && !_agentClient.LinkStop())
            {
                errors.Add("MaaAgentClient.LinkStop 返回 false");
            }
        }
        catch (Exception exception)
        {
            errors.Add($"LinkStop: {exception.GetBaseException().Message}");
        }

        if (_agentProcess is { } agentProcess)
        {
            try
            {
                if (!agentProcess.HasExited)
                {
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    grace.CancelAfter(AgentExitGracePeriod);
                    try
                    {
                        await agentProcess.WaitForExitAsync(grace.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        forced = true;
                        agentProcess.Kill(entireProcessTree: true);
                        await agentProcess.WaitForExitAsync(CancellationToken.None);
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Add($"Agent process: {exception.GetBaseException().Message}");
            }
        }

        try
        {
            _agentClient?.Dispose();
            _tasker?.Dispose();
            _resource?.Dispose();
            _controller?.Dispose();
            _agentProcess?.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add($"Dispose: {exception.GetBaseException().Message}");
        }
        finally
        {
            _agentClient = null;
            _tasker = null;
            _resource = null;
            _controller = null;
            _agentProcess = null;
        }

        return (errors.Count == 0, forced, errors.Count == 0 ? null : string.Join("；", errors));
    }

    private void ReportRunningOnce()
    {
        lock (_gate)
        {
            if (_runningReported)
            {
                return;
            }
            _runningReported = true;
        }
        _onRunning();
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var result)
            && Enum.IsDefined(result))
        {
            return result;
        }
        throw new InvalidDataException($"无法映射 MaaFramework {typeof(T).Name}：{value}。 ");
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "unknown";
        }
        catch
        {
            return "unavailable";
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}

internal sealed class StopConfirmationException : Exception
{
    internal StopConfirmationException(string message) : base(message)
    {
    }

    internal StopConfirmationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
