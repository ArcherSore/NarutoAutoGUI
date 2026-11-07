namespace NarutoAutoGUI.Infrastructure;

internal sealed record DebugFileUsage(int Files, long Bytes);

internal sealed record DebugFileCleanupResult(int Deleted, long ReclaimedBytes);

// Deletes on the user's request only; automatic retention belongs to Serilog and MaaFramework's LogDir cleanup.
internal sealed class DebugFileCleaner(string applicationDirectory, string logDirectory, AppLogger logger)
{
    internal DebugFileUsage MeasureUsage()
    {
        var files = Candidates().ToArray();
        return new DebugFileUsage(files.Length, files.Sum(file => file.Length));
    }

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
        var applicationLogs = Path.Combine(applicationDirectory, "logs");
        return Logs(logDirectory, keepNewest: true)
            .Concat(PathsEqual(applicationLogs, logDirectory) ? [] : Logs(applicationLogs, keepNewest: false))
            .Concat(MaaDebugImages.EnumerateLogs(applicationDirectory, Skip))
            .Concat(MaaDebugImages.Enumerate(applicationDirectory, Skip))
            .Concat(MaaDebugImages.EnumerateExports(applicationDirectory, Skip));
    }

    // Deleting the GUI log the logger still writes would lose everything written after it. The newest file is that log.
    private static IEnumerable<FileInfo> Logs(string directory, bool keepNewest)
    {
        if (!Directory.Exists(directory)) {
            return [];
        }
        var files = new DirectoryInfo(directory).GetFiles("*.log")
            .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToArray();
        var active = keepNewest
            ? files.Where(file => file.Name.StartsWith("NarutoAutoGUI-", StringComparison.OrdinalIgnoreCase))
                .MaxBy(file => file.LastWriteTimeUtc)
            : null;
        return files.Where(file => file.FullName != active?.FullName);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

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
