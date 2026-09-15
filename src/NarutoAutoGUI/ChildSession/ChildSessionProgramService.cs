using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.ChildSession;

internal sealed class ChildSessionProgramService
{
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(10);
    private readonly AppLogger _logger;

    internal ChildSessionProgramService(AppLogger logger)
    {
        _logger = logger;
    }

    internal async Task LaunchIfNeededAsync(
        uint childSessionId, string executablePath, string arguments = "",
        CancellationToken cancellationToken = default, string? launchedProcessName = null)
    {
        var fullPath = ValidateExecutablePath(executablePath);
        var workDir = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        var processName = Path.GetFileName(fullPath);

        var existing = FindLaunchProcess(
            ChildSessionNativeMethods.EnumerateProcesses(), childSessionId, processName, launchedProcessName);
        if (existing.ProcessId != 0) {
            _logger.Info($"跳过重复启动：{existing.Name} 已在 Child Session {childSessionId} 中运行，PID={existing.ProcessId}。");
            return;
        }

        _logger.Info($"正在 Child Session {childSessionId} 中启动：{fullPath}");
        _logger.Debug($"启动参数：{arguments}；工作目录：{workDir}。");
        try {
            await ChildSessionProcessLauncher.LaunchAsync(childSessionId, fullPath, arguments, workDir);
            _logger.Debug($"Task Scheduler COM RunEx 已提交：{processName}，SessionId={childSessionId}。");
        } catch (Exception exception) {
            _logger.Error($"程序启动请求失败：{fullPath}，Child Session={childSessionId}。", exception);
            throw;
        }

        var deadline = DateTime.UtcNow + VerificationTimeout;
        while (DateTime.UtcNow < deadline) {
            cancellationToken.ThrowIfCancellationRequested();
            var launched = FindLaunchProcess(
                ChildSessionNativeMethods.EnumerateProcesses(), childSessionId, processName, launchedProcessName);
            if (launched.ProcessId != 0) {
                _logger.Info($"程序启动验证成功：{launched.Name}，PID={launched.ProcessId}，SessionId={childSessionId}。");
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        var exceptionMessage =
            $"已提交 {processName} 启动请求，但 {VerificationTimeout.TotalSeconds:0} 秒内未在 Child Session "
            + $"{childSessionId} 中枚举到 {processName}"
            + (launchedProcessName is null ? "。" : $" 或 {launchedProcessName}。")
            + "请打开完整桌面检查启动提示，确认微端可以正常启动后重试准备运行环境。";
        _logger.Warn(exceptionMessage);
        throw new InvalidOperationException(exceptionMessage);
    }

    internal static (uint ProcessId, uint SessionId, string Name) FindLaunchProcess(
        IEnumerable<(uint ProcessId, uint SessionId, string Name)> processes,
        uint sessionId, string launcherName, string? launchedProcessName)
    {
        // A bootstrap launcher may exit before the first poll while its client keeps running.
        return processes.FirstOrDefault(process => process.SessionId == sessionId && process.ProcessId != 0
            && (string.Equals(process.Name, launcherName, StringComparison.OrdinalIgnoreCase)
                || launchedProcessName is not null
                && string.Equals(process.Name, launchedProcessName, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) {
            throw new ArgumentException("executable 路径不能为空。", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("指定的程序不存在。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("当前只允许启动 .exe 程序。", nameof(executablePath));
        }

        return fullPath;
    }

}
