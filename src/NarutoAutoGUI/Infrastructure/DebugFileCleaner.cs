namespace NarutoAutoGUI.Infrastructure;

internal sealed record DebugFileCleanupResult(int Deleted, long ReclaimedBytes);

// Deletes on the user's request only; automatic retention belongs to Serilog and MaaFramework's LogDir cleanup.
internal sealed class DebugFileCleaner(string applicationDirectory, string logDirectory, AppLogger logger)
{
    internal long MeasureBytes() => Candidates().Sum(file => file.Length);

    internal DebugFileCleanupResult Clean()
    {
        var deleted = 0;
        long reclaimed = 0;
        foreach (var file in Candidates().ToArray()) {
            try {
                var length = file.Length;
                file.Delete();
                deleted++;
                reclaimed += length;
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                // A running Worker holds maafw.log open; it goes once the Worker has exited.
                logger.Debug($"调试文件未删除{AppLogger.Field("file", file.FullName)}"
                    + AppLogger.Field("error", exception.Message));
            }
        }
        RemoveEmptyExports();
        logger.Info($"已清理日志和调试文件{AppLogger.Field("deleted", deleted)}{AppLogger.Field("bytes", reclaimed)}");
        return new DebugFileCleanupResult(deleted, reclaimed);
    }

    internal static string FormatBytes(long bytes) => bytes < 1024 * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024):F1} MiB" : $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";

    private IEnumerable<FileInfo> Candidates()
    {
        void Skip(string entry, Exception exception) => logger.Warn($"调试文件清理跳过：{entry}。", exception);
        var logs = Logs(logDirectory);
        // The GUI log being written stays, so it is not counted as cleanable. The newest file is that log.
        var active = logs.Where(file => file.Name.StartsWith("NarutoAutoGUI-", StringComparison.OrdinalIgnoreCase))
            .MaxBy(file => file.LastWriteTimeUtc);
        return logs.Concat(Logs(Path.Combine(applicationDirectory, "logs")))
            .DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Where(file => file.FullName != active?.FullName)
            .Concat(MaaDebugImages.EnumerateRemovable(applicationDirectory, Skip));
    }

    private FileInfo[] Logs(string directory)
    {
        try {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).GetFiles("*.log")
                    .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToArray()
                : [];
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            logger.Debug($"日志目录未清理{AppLogger.Field("directory", directory)}"
                + AppLogger.Field("error", exception.Message));
            return [];
        }
    }

    private void RemoveEmptyExports()
    {
        var exports = MaaDebugImages.ExportsDirectory(applicationDirectory);
        if (!Directory.Exists(exports) || File.GetAttributes(exports).HasFlag(FileAttributes.ReparsePoint)) {
            return;
        }
        foreach (var directory in Directory.GetDirectories(exports)) {
            try {
                // Not recursive: a skipped file keeps its export folder.
                Directory.Delete(directory);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                logger.Debug($"诊断导出目录未删除{AppLogger.Field("directory", directory)}"
                    + AppLogger.Field("error", exception.Message));
            }
        }
    }
}
