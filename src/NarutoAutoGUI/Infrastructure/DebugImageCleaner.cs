namespace NarutoAutoGUI.Infrastructure;

internal sealed record DebugImageCleanupResult(int Deleted, long ReclaimedBytes, int Remaining, long RemainingBytes,
    int Failed);

// Prunes MaaFramework debug images; logs, ZIPs and unrelated files under debug are never touched.
internal sealed class DebugImageCleaner(string applicationDirectory, AppLogger logger)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan RecentProtection = TimeSpan.FromMinutes(2);
    private const long HighWatermarkBytes = 512L * 1024 * 1024;
    private const long LowWatermarkBytes = 256L * 1024 * 1024;

    // Cleans now and then every Interval; passes run one after another, so they never overlap.
    internal async Task RunAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(Interval);
        try {
            do {
                try {
                    await Task.Run(() => Clean(DateTime.UtcNow), cancellation);
                } catch (Exception exception) when (exception is not OperationCanceledException) {
                    logger.Warn("调试截图清理失败。", exception);
                }
            } while (await timer.WaitForNextTickAsync(cancellation));
        } catch (OperationCanceledException) {
            // The application is exiting.
        }
    }

    internal DebugImageCleanupResult Clean(DateTime nowUtc, long highWatermark = HighWatermarkBytes,
        long lowWatermark = LowWatermarkBytes)
    {
        // One scan per pass, oldest first.
        var files = MaaDebugImages.Enumerate(applicationDirectory,
                (entry, exception) => logger.Warn($"调试截图清理跳过：{entry}。", exception))
            .Select(file => (file.FullName, file.Length, file.LastWriteTimeUtc))
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var remaining = files.Sum(file => file.Length);
        var protectedAfter = nowUtc - RecentProtection;
        var expiredBefore = nowUtc - MaximumAge;
        var deleted = 0;
        var failed = 0;
        long reclaimed = 0;
        bool? trimming = null;
        foreach (var file in files) {
            if (file.LastWriteTimeUtc >= protectedAfter) {
                break;
            }
            if (file.LastWriteTimeUtc >= expiredBefore) {
                // Expired files sort first, so the capacity decision sees the size left after the age rule.
                trimming ??= remaining > highWatermark;
                if (!trimming.Value || remaining <= lowWatermark) {
                    break;
                }
            }
            try {
                File.Delete(file.FullName);
                deleted++;
                reclaimed += file.Length;
                remaining -= file.Length;
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                if (failed++ == 0) {
                    logger.Warn($"调试截图无法删除，已跳过：{file.FullName}。", exception);
                }
            }
        }
        var result = new DebugImageCleanupResult(deleted, reclaimed, files.Length - deleted, remaining, failed);
        var summary = $"调试截图清理：删除 {deleted} 个，释放 {ToMiB(reclaimed)}；"
            + $"剩余 {result.Remaining} 个，共 {ToMiB(remaining)}" + (failed > 0 ? $"；{failed} 个无法删除。" : "。");
        if (deleted > 0 || failed > 0) {
            logger.Info(summary);
        } else {
            logger.Debug(summary);
        }
        return result;
    }

    private static string ToMiB(long bytes) => $"{bytes / (1024.0 * 1024):F1} MiB";
}
