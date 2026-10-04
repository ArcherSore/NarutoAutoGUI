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
            if (defaults != new MaaFrameworkOptions()) {
                throw new InvalidOperationException("省略框架选项必须保留 true/false/false 默认值。");
            }
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
        Console.WriteLine("FRAMEWORK OPTIONS SELF-TEST PASS: defaults, explicit paths, rejected options and NotReady.");
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
        using var host = new WorkerHost(new WorkerArguments(instance, new string('t', 32), "unused"), manifest);
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
