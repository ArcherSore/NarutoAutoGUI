using Gui.ChildSession;
using Gui.Infrastructure;

namespace Gui.Worker;

internal sealed class ChildSessionWorkerLauncher
{
    private static readonly TimeSpan ProcessVerificationTimeout = TimeSpan.FromSeconds(30);
    private readonly AppLogger _logger;

    internal ChildSessionWorkerLauncher(AppLogger logger)
    {
        _logger = logger;
    }

    internal async Task<VerifiedChildSessionProcessLaunch> LaunchAsync(
        uint childSessionId, string workerExecutablePath, Guid workerInstanceId,
        string launchToken, string manifestPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(workerExecutablePath);
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Worker 尚未随 GUI 发布。请重新运行正式发布脚本。", fullPath);
        }
        _logger.Info(
            $"正在 Child Session {childSessionId} 启动 Worker，instance={workerInstanceId}；启动凭据已隐藏。 ");
        var arguments = FormatArguments(workerInstanceId, launchToken, manifestPath);
        try {
            var result = await ChildSessionProcessLauncher.LaunchElevatedVerifiedAsync(
                childSessionId, fullPath, arguments,
                Path.GetDirectoryName(fullPath), ProcessVerificationTimeout, cancellationToken);
            _logger.Info(
                $"Worker 启动验证成功：PID={result.ProcessId}，SessionId={result.SessionId}，"
                + $"TaskState={FormatDiagnostic(result.TaskState)}，"
                + $"LastTaskResult={FormatDiagnostic(result.LastTaskResult)}。 ");
            return result;
        } catch (Exception exception) {
            _logger.Error(
                $"Worker Task Scheduler 启动或进程验证失败：Child Session={childSessionId}，"
                + $"instance={workerInstanceId}；启动凭据已隐藏。 ",
                exception);
            throw;
        }
    }

    internal static string FormatArguments(Guid workerInstanceId, string launchToken, string manifestPath) =>
        $"--instance {workerInstanceId:D} --token {launchToken} --manifest \"{manifestPath}\"";

    private static string FormatDiagnostic(int? value) => value is int actual
        ? $"{actual} (0x{unchecked((uint)actual):X8})"
        : "unavailable";
}
