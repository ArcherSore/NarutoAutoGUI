using System.Security.AccessControl;
using System.Security.Principal;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyDebugFileCleanup(AppLogger logger, string testDirectory)
    {
        var application = Path.Combine(testDirectory, "debug-file-cleanup");
        var logs = Path.Combine(application, "logs");
        string Create(string relative, int ageHours = 0) =>
            WriteFixture(application, relative, new byte[100], ageHours * 60);
        string[] removed = [
            Create("logs/NarutoAutoGUI-old.log", ageHours: 2), Create("logs/updater.log", ageHours: 3),
            Create("debug/maafw.bak.old.log"), Create("debug/nested/custom.log"), Create("debug/vision/draw.jpg"),
            Create("debug/on_error/error.png"), Create("debug/screencap/nested/frame.jpeg"),
            Create("debug/exports/MaaNOP-logs-20260101-000000/part01.zip")
        ];
        var activeLog = Create("logs/NarutoAutoGUI-active.log", ageHours: 0);
        string[] preserved = [
            activeLog, Create("config/maanop-config.json"), Create("resource/image.png"), Create("debug/notes.txt"),
            Create("debug/vision/other.png"), Create("debug/on_error/other.jpg")
        ];

        var cleaner = new DebugFileCleaner(application, logs, logger);
        var bytes = cleaner.MeasureBytes();
        Require(bytes == removed.Length * 100L, $"占用应只统计将被清理的文件：{bytes}");
        var locked = Create("debug/maafw.log");
        var result = default(DebugFileCleanupResult);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            result = cleaner.Clean();
        }
        Require(File.Exists(locked), "被占用的日志应跳过而不是中断清理。");
        Require(result == new DebugFileCleanupResult(removed.Length, removed.Length * 100L),
            $"释放的空间只应计入实际删除的文件：{result}");
        Require(!removed.Any(File.Exists), "约定范围内的文件都应被清理。");
        Require(preserved.All(File.Exists), "正在写入的日志、配置、资源和白名单以外的文件不得删除。");
        Require(!Directory.Exists(Path.GetDirectoryName(removed[^1])!), "清空后的诊断包目录应一并删除。");

        // With the GUI log elsewhere, nothing in the application's logs folder is that active file.
        var fallback = Path.Combine(testDirectory, "debug-file-fallback-logs");
        Directory.CreateDirectory(fallback);
        File.WriteAllBytes(Path.Combine(fallback, "NarutoAutoGUI-active.log"), new byte[100]);
        new DebugFileCleaner(application, fallback, logger).Clean();
        Require(File.Exists(Path.Combine(fallback, "NarutoAutoGUI-active.log")) && !File.Exists(activeLog),
            "日志回退到其他目录时，应保留实际目录中正在写入的日志。");

        var deniedFolder = new DirectoryInfo(logs);
        var denied = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var security = deniedFolder.GetAccessControl();
        security.AddAccessRule(denied);
        deniedFolder.SetAccessControl(security);
        try {
            var image = Create("debug/vision/after.jpg");
            Require(cleaner.MeasureBytes() >= 100, "logs 无法读取时，仍应统计其他可清理的文件。");
            cleaner.Clean();
            Require(!File.Exists(image), "logs 无法读取时，应跳过它并继续清理其他文件。");
        } finally {
            security.RemoveAccessRule(denied);
            deniedFolder.SetAccessControl(security);
        }
    }
}
