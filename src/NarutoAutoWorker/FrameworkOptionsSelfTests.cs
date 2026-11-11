using System.Text.Json;
using MaaFramework.Binding;
using MaaFramework.Binding.Abstractions;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class FrameworkOptionsSelfTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"NarutoFrameworkOptions-{Guid.NewGuid():N}");
        try {
            var defaults = JsonSerializer.Deserialize<MaaFrameworkOptions>("{}", ProtocolJson.Options)!;
            if (defaults != new MaaFrameworkOptions(SaveOnError: true, SaveDraw: false, DebugMode: false)) {
                throw new InvalidOperationException("省略框架选项时，失败截图默认开启，标注图和调试模式默认关闭。");
            }
            VerifyRuntimeSwitches();
            VerifyRuntimeRequest().GetAwaiter().GetResult();
            var log = new List<(string Level, string Message)>();
            var options = new MaaFrameworkOptions(false, true, true);
            var framework = new FakeOptions();
            WorkerFrameworkOptions.Apply(root, options, (level, _, message) => log.Add((level, message)), framework);
            if (!framework.Values.SequenceEqual(new (GlobalOption, object?)[] {
                (GlobalOption.LogDir, Path.Combine(root, "debug")), (GlobalOption.SaveOnError, false),
                (GlobalOption.SaveDraw, true), (GlobalOption.DebugMode, true)
            }) || log.Count != 4 || log.Any(entry => entry.Level != "INFO")
                || Directory.EnumerateFiles(Path.Combine(root, "debug")).Any()) {
                throw new InvalidOperationException("框架选项必须按序应用，记录结果并清理可写性探针。");
            }
            foreach (var rejected in new[] {
                GlobalOption.LogDir, GlobalOption.SaveOnError, GlobalOption.SaveDraw, GlobalOption.DebugMode
            }) {
                framework = new FakeOptions { Rejected = rejected };
                log.Clear();
                try {
                    WorkerFrameworkOptions.Apply(root, options,
                        (level, _, message) => log.Add((level, message)), framework);
                    throw new InvalidOperationException("SetOption=false 不得静默继续。");
                } catch (InvalidOperationException exception) when (exception.Message.Contains("SetOption=false")) {
                    if (framework.Values.Last().Option != rejected || log.Last().Level != "ERROR"
                        || !log.Last().Message.Contains(rejected.ToString(), StringComparison.Ordinal)) {
                        throw new InvalidOperationException("设置失败须在该选项停止并记录明确的错误。");
                    }
                }
            }
            VerifyNotReady(root);
        } finally {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Setting LogDir on a running Worker would reopen its log and start MaaFramework's cleanup.
    private static void VerifyRuntimeSwitches()
    {
        var framework = new FakeOptions();
        WorkerFrameworkOptions.ApplySwitches(new MaaFrameworkOptions(false, true, true), (_, _, _) => { }, framework);
        if (!framework.Values.SequenceEqual(new (GlobalOption, object?)[] {
            (GlobalOption.SaveOnError, false), (GlobalOption.SaveDraw, true), (GlobalOption.DebugMode, true)
        })) {
            throw new InvalidOperationException("运行中更新只能设置三个开关，不能重设 LogDir。");
        }
    }

    private static async Task VerifyRuntimeRequest()
    {
        await using var harness = await RunHarness.CreateAsync();
        var options = new MaaFrameworkOptions(false, true, true);
        var request = new FrameworkSetOptionsRequest(options, TelemetryEnabled: false);
        var response = await harness.RequestAsync(ProtocolOperations.FrameworkSetOptions, request);
        if (response.Success != true || harness.AppliedFrameworkOptions.SingleOrDefault() != options) {
            throw new InvalidOperationException("framework.setOptions 必须把选项应用到正在运行的 Worker。");
        }
        harness.FrameworkOptionsFailure = new InvalidOperationException("SetOption=false");
        response = await harness.RequestAsync(ProtocolOperations.FrameworkSetOptions, request);
        if (response.Success != false) {
            throw new InvalidOperationException("选项设置失败时不能向 GUI 报告成功。");
        }
    }

    private static void VerifyNotReady(string root)
    {
        var blockedRoot = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockedRoot, "blocks LogDir creation");
        var instance = Guid.NewGuid();
        var manifest = new LaunchManifest(ProtocolConstants.LaunchContextVersion, instance, "unused", blockedRoot,
            new ProjectProvenance("test", "1", 1, "unused"),
            new Win32ControllerDefinition("test", "class", "window", "Cache", "Send", "Send"),
            [new ResourceDefinition("test", [blockedRoot])], new AgentDefinition("unused", [], blockedRoot));
        var host = new WorkerHost(new WorkerArguments(instance, new string('t', 32), "unused"), manifest);
        host.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var snapshot = host.GetSnapshot();
        if (snapshot.WorkerState != WorkerState.NotReady || snapshot.WorkerReason?.Code != "FrameworkOptionsFailed"
            || snapshot.LastLogSequence < 1) {
            throw new InvalidOperationException("LogDir 不可用时必须记录错误并保持 Worker NotReady。");
        }
    }

    private sealed class FakeOptions : IMaaOption<GlobalOption>
    {
        internal GlobalOption? Rejected { get; init; }
        internal List<(GlobalOption Option, object? Value)> Values { get; } = [];

        public bool SetOption<T>(GlobalOption opt, T value)
        {
            Values.Add((opt, value));
            return opt != Rejected;
        }
    }
}
