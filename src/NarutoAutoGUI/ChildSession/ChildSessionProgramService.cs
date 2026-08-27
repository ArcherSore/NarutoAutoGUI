using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.ChildSession;

internal sealed record ProgramLaunchResult(bool Launched, uint ProcessId, string ProcessName);

internal sealed class ChildSessionProgramService
{
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(10);
    private readonly AppLogger _logger;

    internal ChildSessionProgramService(AppLogger logger)
    {
        _logger = logger;
    }

    internal async Task<ProgramLaunchResult> LaunchIfNeededAsync(
        uint childSessionId, string executablePath, string arguments = "",
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateExecutablePath(executablePath);
        var workDir = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        var processName = Path.GetFileName(fullPath);

        if (ChildSessionNativeMethods.TryFindProcessInSession(processName, childSessionId, out var existingPid)) {
            _logger.Info($"跳过重复启动：{processName} 已在 Child Session {childSessionId} 中运行，PID={existingPid}。");
            return new ProgramLaunchResult(false, existingPid, processName);
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
            if (ChildSessionNativeMethods.TryFindProcessInSession(processName, childSessionId, out var pid)) {
                _logger.Info($"程序启动验证成功：{processName}，PID={pid}，SessionId={childSessionId}。");
                return new ProgramLaunchResult(true, pid, processName);
            }

            await Task.Delay(500, cancellationToken);
        }

        var exceptionMessage =
            $"已提交 {processName} 启动请求，但 {VerificationTimeout.TotalSeconds:0} 秒内未在 Child Session "
            + $"{childSessionId} 中枚举到目标进程。";
        _logger.Warn(exceptionMessage);
        throw new InvalidOperationException(exceptionMessage);
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
