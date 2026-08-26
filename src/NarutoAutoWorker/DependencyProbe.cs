using System.Diagnostics;
using System.Text.Json;
using MaaFramework.Binding;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class DependencyProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<(DependencyStatus Status, StructuredReason? Reason)> RunAsync(
        LaunchManifest manifest, CancellationToken cancellationToken)
    {
        DependencyCheck framework;
        var bindingVersion = typeof(MaaGlobal).Assembly.GetName().Version?.ToString() ?? "unknown";
        string runtimeVersion;
        try {
            runtimeVersion = NativeBindingContext.LibraryVersion;
            framework = new DependencyCheck(true, runtimeVersion, null);
        } catch (Exception exception) {
            runtimeVersion = "unavailable";
            framework = new DependencyCheck(false, null, exception.GetBaseException().Message);
        }

        var resourceErrors = manifest.Resources
            .SelectMany(resource => resource.Paths)
            .Where(path => !Directory.Exists(path))
            .ToArray();
        if (resourceErrors.Length > 0) {
            var reason = new StructuredReason("ResourceInvalid", $"Resource 目录不存在：{string.Join(", ", resourceErrors)}");
            return (CreateUnavailableStatus(bindingVersion, runtimeVersion, reason.Message), reason);
        }

        var entryPath = ResolveAgentEntryPath(manifest.Agent);
        var entryCheck = File.Exists(entryPath)
            ? new DependencyCheck(true, entryPath, null)
            : new DependencyCheck(false, entryPath, "Agent 入口不存在。 ");

        ProbePayload? payload = null;
        string? probeError = null;
        try {
            payload = await RunPythonProbeAsync(manifest.Agent, entryPath, cancellationToken);
        } catch (Exception exception) {
            probeError = exception.GetBaseException().Message;
        }

        var python = payload is null
            ? new DependencyCheck(false, null, probeError)
            : new DependencyCheck(true, $"{payload.Executable} | {payload.Version}", null);
        var maa = ImportCheck(payload?.Maa, probeError);
        var agentServer = ImportCheck(payload?.AgentServer, probeError);
        var toolkit = ImportCheck(payload?.Toolkit, probeError);
        var status = new DependencyStatus(
            DateTime.UtcNow, bindingVersion, runtimeVersion, python,
            maa, agentServer, toolkit, entryCheck);

        var failures = new[]
            {
                ("MaaFrameworkUnavailable", framework), ("PythonMissing", python),
                ("AgentModuleMissing", maa), ("AgentServerMissing", agentServer),
                ("ToolkitMissing", toolkit), ("AgentEntryInvalid", entryCheck)
            }
            .Where(item => !item.Item2.Success)
            .ToArray();
        var reasonResult = failures.Length == 0
            ? null
            : new StructuredReason(
                failures[0].Item1,
                string.Join("；", failures.Select(item => item.Item2.Error ?? item.Item1)));
        return (status, reasonResult);
    }

    private static async Task<ProbePayload> RunPythonProbeAsync(AgentDefinition agent, string entryPath, CancellationToken cancellationToken)
    {
        const string script = "import json,sys; result={'executable':sys.executable,'version':sys.version," +
                              "'maa':False,'agentServer':False,'toolkit':False}; import maa; result['maa']=True; " +
                              "from maa.agent.agent_server import AgentServer; result['agentServer']=True; " +
                              "from maa.toolkit import Toolkit; result['toolkit']=True; " +
                              "print(json.dumps(result,ensure_ascii=False))";
        var startInfo = new ProcessStartInfo {
            FileName = agent.ChildExec,
            WorkingDirectory = agent.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 Python Agent Probe。 ");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try {
            await process.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Python Agent Probe 超过 {ProbeTimeout.TotalSeconds:0} 秒。 ");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                $"Python Agent Probe 退出码 {process.ExitCode}：{stderr.Trim()}。 ");
        }
        return JsonSerializer.Deserialize<ProbePayload>(stdout.Trim(), ProtocolJson.Options)
               ?? throw new InvalidDataException("Python Agent Probe 未返回 JSON。 ");
    }

    private static string ResolveAgentEntryPath(AgentDefinition agent)
    {
        var candidate = agent.ChildArgs.FirstOrDefault(argument =>
            !argument.StartsWith("-", StringComparison.Ordinal)
            && argument.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        if (candidate is null) {
            throw new InvalidDataException("Agent child_args 中没有 Python 入口脚本。 ");
        }
        return Path.GetFullPath(
            Path.IsPathFullyQualified(candidate)
                ? candidate
                : Path.Combine(agent.WorkingDirectory, candidate));
    }

    private static DependencyCheck ImportCheck(bool? success, string? error) =>
        success == true
            ? new DependencyCheck(true, "import ok", null)
            : new DependencyCheck(false, null, error ?? "import failed");

    private static DependencyStatus CreateUnavailableStatus(string bindingVersion, string runtimeVersion, string error)
    {
        var failed = new DependencyCheck(false, null, error);
        return new DependencyStatus(
            DateTime.UtcNow, bindingVersion, runtimeVersion, failed,
            failed, failed, failed, failed);
    }

    private sealed record ProbePayload(string Executable, string Version, bool Maa, bool AgentServer, bool Toolkit);
}
