using MaaFramework.Binding;
using MaaFramework.Binding.Abstractions;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class WorkerFrameworkOptions
{
    internal static void Apply(string projectRoot, MaaFrameworkOptions options, Action<string, string, string> log,
        IMaaOption<GlobalOption>? framework = null)
    {
        // Match the GUI diagnostic export path, independently of the Worker's current directory.
        var logDirectory = Path.GetFullPath(Path.Combine(projectRoot, "debug"));
        Directory.CreateDirectory(logDirectory);
        // SetOption(LogDir) does not guarantee that subsequent image writes will succeed.
        using (new FileStream(Path.Combine(logDirectory, $".write-probe-{Guid.NewGuid():N}"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }

        framework ??= MaaGlobal.Shared;
        SetOption(framework, GlobalOption.LogDir, logDirectory, log);
        ApplySwitches(options, log, framework);
    }

    // MaaFramework reads these per recognition, so they also apply to a running Worker. Setting LogDir again would
    // reopen the log and start its cleanup, which is why a change at runtime stops here.
    internal static void ApplySwitches(MaaFrameworkOptions options, Action<string, string, string> log,
        IMaaOption<GlobalOption>? framework = null)
    {
        framework ??= MaaGlobal.Shared;
        SetOption(framework, GlobalOption.SaveOnError, options.SaveOnError, log);
        SetOption(framework, GlobalOption.SaveDraw, options.SaveDraw, log);
        SetOption(framework, GlobalOption.DebugMode, options.DebugMode, log);
    }

    private static void SetOption<T>(IMaaOption<GlobalOption> framework, GlobalOption option, T value,
        Action<string, string, string> log)
    {
        var success = framework.SetOption(option, value);
        log(success ? "INFO" : "ERROR", "runtime.options",
            $"MaaFramework {option}={value}；SetOption={success}；PID={Environment.ProcessId}。");
        if (!success) {
            throw new InvalidOperationException($"MaaFramework {option} 设置失败（SetOption=false）。");
        }
    }
}
