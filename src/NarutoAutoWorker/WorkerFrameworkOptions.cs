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
        SetOption(GlobalOption.LogDir, logDirectory);
        SetOption(GlobalOption.SaveOnError, options.SaveOnError);
        SetOption(GlobalOption.SaveDraw, options.SaveDraw);
        SetOption(GlobalOption.DebugMode, options.DebugMode);

        void SetOption<T>(GlobalOption option, T value)
        {
            var success = framework.SetOption(option, value);
            log(success ? "INFO" : "ERROR", "runtime.options",
                $"MaaFramework {option}={value}；SetOption={success}；PID={Environment.ProcessId}。");
            if (!success) {
                throw new InvalidOperationException($"MaaFramework {option} 设置失败（SetOption=false）。");
            }
        }
    }
}
