using MaaNOP.ChildSessionLauncher;
using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.Worker;

internal sealed class ChildSessionWorkerLauncher
{
    private readonly AppLogger _logger;

    internal ChildSessionWorkerLauncher(AppLogger logger)
    {
        _logger = logger;
    }

    internal async Task LaunchAsync(
        uint childSessionId,
        string workerExecutablePath,
        Guid workerInstanceId,
        string launchToken,
        string manifestPath)
    {
        var fullPath = Path.GetFullPath(workerExecutablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "NarutoAutoWorker 尚未随 GUI 发布。请重新运行正式发布脚本。",
                fullPath);
        }
        _logger.Info(
            $"正在 Child Session {childSessionId} 启动 NarutoAutoWorker，instance={workerInstanceId}；启动凭据已隐藏。 ");
        var arguments = $"--instance {workerInstanceId:D} --token {launchToken} --manifest \"{manifestPath}\"";
        await ChildSessionProcessLauncher.LaunchElevatedAsync(
            childSessionId,
            fullPath,
            arguments,
            Path.GetDirectoryName(fullPath));
    }
}
