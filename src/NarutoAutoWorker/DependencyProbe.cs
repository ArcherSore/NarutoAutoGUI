using MaaFramework.Binding;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class DependencyProbe
{
    internal static (DependencyStatus Status, StructuredReason? Reason) Run(LaunchManifest manifest)
    {
        DependencyCheck framework;
        var bindingVersion = typeof(MaaGlobal).Assembly.GetName().Version?.ToString() ?? "unknown";
        string runtimeVersion;
        try {
            runtimeVersion = NativeBindingContext.LibraryVersion;
            framework = new DependencyCheck(true, runtimeVersion, null);
        } catch (Exception exception) {
            runtimeVersion = "unavailable";
            framework = new DependencyCheck(false, null, $"MaaFramework 无法加载（{exception.GetBaseException().Message}）。"
                + "通常是缺少运行库，请运行安装目录中的 DependencySetup_依赖库安装.bat 后重试。");
        }

        var missingResources = manifest.Resources.SelectMany(resource => resource.Paths)
            .Where(path => !Directory.Exists(path)).ToArray();
        var resources = missingResources.Length == 0
            ? new DependencyCheck(true, null, null)
            : new DependencyCheck(false, null, $"Resource 目录不存在：{string.Join(", ", missingResources)}");
        DependencyCheck executable;
        try {
            var path = ResolveAgentExecutablePath(manifest.Agent);
            executable = File.Exists(path)
                ? new DependencyCheck(true, path, null)
                : new DependencyCheck(false, path, $"Agent 可执行程序不存在：{path}");
        } catch (Exception exception) when (exception is IOException or ArgumentException
            or UnauthorizedAccessException) {
            executable = new DependencyCheck(false, manifest.Agent.ChildExec, exception.Message);
        }

        var status = new DependencyStatus(DateTime.UtcNow, bindingVersion, runtimeVersion,
            framework, resources, executable);
        var failures = new[] {
            ("MaaFrameworkUnavailable", framework), ("ResourceInvalid", resources),
            ("AgentExecutableMissing", executable)
        }.Where(item => !item.Item2.Success).ToArray();
        var reason = failures.Length == 0 ? null : new StructuredReason(failures[0].Item1,
            string.Join("；", failures.Select(item => item.Item2.Error ?? item.Item1)));
        return (status, reason);
    }

    internal static string ResolveAgentExecutablePath(AgentDefinition agent) =>
        PretaskProcess.ResolveExecutable(agent.ChildExec, agent.WorkingDirectory);
}
