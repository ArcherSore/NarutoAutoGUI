namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyDebugImageCleanup(AppLogger logger, string testDirectory)
    {
        var now = DateTime.UtcNow;
        var application = Path.Combine(testDirectory, "debug-image-cleanup");
        var debug = Path.Combine(application, "debug");
        string Create(string relative, TimeSpan age, int length = 100)
        {
            var path = Path.Combine(debug, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[length]);
            File.SetLastWriteTimeUtc(path, now - age);
            return path;
        }
        var expired = TimeSpan.FromHours(25);
        string[] expiredImages = [
            Create("vision/old.jpg", expired), Create("on_error/old.png", expired),
            Create("screencap/nested/old.jpeg", expired)
        ];
        string[] preserved = [
            Create("vision/other.png", expired), Create("on_error/other.jpg", expired), Create("maafw.log", expired),
            Create("diagnostics.zip", expired), Create("vision/recent.jpg", TimeSpan.FromMinutes(1))
        ];
        var outside = Path.Combine(testDirectory, "debug-image-outside");
        var linked = Path.Combine(outside, "linked.jpg");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(linked, new byte[100]);
        File.SetLastWriteTimeUtc(linked, now - expired);
        CreateJunction(Path.Combine(debug, "vision", "link"), outside);
        var cleaner = new DebugImageCleaner(application, logger);
        var result = cleaner.Clean(now);
        if (result != new DebugImageCleanupResult(3, 300, 1, 100, 0) || expiredImages.Any(File.Exists)
            || !preserved.All(File.Exists) || !File.Exists(linked)) {
            throw new InvalidOperationException("调试截图只能按年龄清理白名单图片，不得跟随链接或删除日志、ZIP 与近期截图。");
        }

        // 100-byte files stand in for the fixed watermarks: trim the oldest from 500 to 250 bytes or below.
        var images = Enumerable.Range(1, 4)
            .Select(hours => Create($"screencap/size-{hours}.png", TimeSpan.FromHours(5 - hours))).ToArray();
        if (cleaner.Clean(now, highWatermark: 500, lowWatermark: 250) != new DebugImageCleanupResult(0, 0, 5, 500, 0)) {
            throw new InvalidOperationException("未超过高水位时不得按容量清理。");
        }
        using (new FileStream(images[0], FileMode.Open, FileAccess.Read, FileShare.Read)) {
            result = cleaner.Clean(now, highWatermark: 350, lowWatermark: 250);
        }
        if (result != new DebugImageCleanupResult(3, 300, 2, 200, 1) || !File.Exists(images[0])
            || images[1..].Any(File.Exists) || !preserved.All(File.Exists)) {
            throw new InvalidOperationException("容量清理必须从最旧开始、跳过无法删除的文件，并在低水位停止。");
        }
        result = cleaner.Clean(now, highWatermark: 0, lowWatermark: 0);
        if (result != new DebugImageCleanupResult(1, 100, 1, 100, 0) || !File.Exists(preserved[^1])) {
            throw new InvalidOperationException("最近 2 分钟内的截图必须受保护，容量阈值只能是软限制。");
        }
        Console.WriteLine("DEBUG IMAGE CLEANUP SELF-TEST PASS: age, watermarks, protection, links and locks.");
    }
}
