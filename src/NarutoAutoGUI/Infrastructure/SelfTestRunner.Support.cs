using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    // Serilog owns rolling and retention; this pins the settings and the line format that diagnosis relies on.
    private static void VerifyFileLogging(string testDirectory)
    {
        // Log names and timestamps must not follow the user's calendar (th-TH) or time separator (fi-FI).
        var culture = CultureInfo.CurrentCulture;
        try {
            foreach (var name in new[] { culture.Name, "th-TH", "fi-FI" }) {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                VerifyFileLoggingInCurrentCulture(Path.Combine(testDirectory, $"file-logging-{name}"));
            }
        } finally {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static void VerifyFileLoggingInCurrentCulture(string directory)
    {
        Directory.CreateDirectory(directory);
        string LogPath(int daysAgo) => Path.Combine(directory, "NarutoAutoGUI-"
            + DateTime.Now.AddDays(-daysAgo).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        var expired = LogPath(20);
        var recent = LogPath(3);
        File.WriteAllText(expired, "expired");
        File.WriteAllText(recent, "recent");

        var logger = new AppLogger(directory);
        var written = new List<LogEntry>();
        logger.EntryWritten += (_, entry) => written.Add(entry);
        logger.Info("""花括号 {"key":"{value}"} {0} {Text}""");
        logger.Error("失败", new InvalidOperationException("file-logging-exception"));
        logger.WriteWorker(LogLevel.Warn, "Worker #1 [run.lifecycle] 转写");
        App.LogStartup(logger);
        var current =Directory.GetFiles(directory, "NarutoAutoGUI-*.log")
            .Single(path => path != expired && path != recent);
        // The diagnostic package reads a log the running logger still holds open.
        var bytes = new MemoryStream();
        using (var source = new FileStream(
            current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            source.CopyTo(bytes);
        }
        var text = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        if (!Regex.IsMatch(text, @"^\[\d\d:\d\d:\d\d\.\d{3}\] \[INFO\] \[T\d+\] "
                + @"\[SelfTestRunner\.Support\.VerifyFileLoggingInCurrentCulture:\d+\] "
                + @"花括号 \{""key"":""\{value\}""\} \{0\} \{Text\}\r?$", RegexOptions.Multiline)
            || !Regex.IsMatch(text, @"\[ERROR\] \[T\d+\] \[[^\]]+\] 失败\r?\nSystem\.InvalidOperationException: "
                + "file-logging-exception")
            || !Regex.IsMatch(text, @"\[WARN\] \[T\d+\] \[Worker\] Worker #1 \[run\.lifecycle\] 转写")
            || !Regex.IsMatch(text, @"\[App\.LogStartup:\d+\] NarutoAutoGUI 正式 GUI 启动 \[version=\S+\] "
                + $@"\[pid={Environment.ProcessId}\] \[session=\d+\] \[elevated=(True|False)\] .*\[logDirectory=")) {
            throw new InvalidOperationException($"日志行格式不符：{text}");
        }
        if (bytes.ToArray() is [0xEF, 0xBB, 0xBF, ..]) {
            throw new InvalidOperationException("日志文件不应带 BOM。");
        }
        if (File.Exists(expired) || !File.Exists(recent)) {
            throw new InvalidOperationException("日志应保留 14 天内的文件并清理更早的文件。");
        }

        var megabyte = new string('x', 1024 * 1024);
        for (var index = 0; index < 11; index++) {
            logger.Debug(megabyte);
        }
        var rolled = Directory.GetFiles(directory, "NarutoAutoGUI-*_001.log");
        // The limit is checked before each write, so a file can pass it by at most the one entry that crossed it.
        if (rolled.Length != 1 || new FileInfo(current).Length > 11L * 1024L * 1024L) {
            var sizes = string.Join("，", Directory.GetFiles(directory).Select(
                path => $"{Path.GetFileName(path)}={new FileInfo(path).Length}"));
            throw new InvalidOperationException($"单个日志文件超过 10 MB 时应滚动到新文件：{sizes}。");
        }

        logger.Dispose();
        var count = written.Count;
        logger.Info("释放后不再写入");
        if (written.Count != count
            || Directory.GetFiles(directory).Any(path => File.ReadAllText(path).Contains("释放后不再写入"))) {
            throw new InvalidOperationException("释放后写日志应被安全忽略。");
        }
    }

    private static void VerifyRunResultLog(string testDirectory)
    {
        using var logger = new AppLogger(Path.Combine(testDirectory, "run-result-log"));
        var written = new List<LogEntry>();
        logger.EntryWritten += (_, entry) => written.Add(entry);
        var log = new RunResultLog(logger);
        var worker = CreateNotificationSnapshot(RunState.Running).WorkerSnapshot!;
        var started = DateTime.UtcNow.AddSeconds(-98);
        PlanItemSnapshot Item(string label, PlanItemState state, DateTime? startedAt = null) => new(
            Guid.NewGuid(), label, label, $"{label}Entry", ProtocolJson.ToElement(new { }),
            ProtocolJson.ToElement(new { }), state, startedAt, null, null, null, null);
        var first = Item("第一项", PlanItemState.Running, started);
        var second = Item("第二项", PlanItemState.Pending);
        var third = Item("第三项", PlanItemState.Pending);
        var running = worker.ActiveRun! with { StartedAtUtc = started, Items = [first, second, third] };
        var active = worker with { ActiveRun = running };

        log.Observe(active);
        log.Observe(active);
        Require(written.Count == 1 && written[0].Level == LogLevel.Info
            && written[0].Message == "→ 任务开始 [task=第一项] [kind=MaaTask] [entry=第一项Entry] [index=1/3]",
            $"重复 Snapshot 不应重复写任务开始：{string.Join(" | ", written)}");

        // A merged change: the second item failed with no Starting or Running snapshot ever seen for it.
        var reason = new StructuredReason("MaaTaskFailed", "MaaFramework task 终态为 Failed。 ");
        var result = ProtocolJson.ToElement(new { maaJobStatus = "Failed" });
        var ended = started.AddSeconds(98);
        var failedRun = running with {
            State = RunState.Failed, EndedAtUtc = ended, Error = reason, Result = result, Items = [
                first with { State = PlanItemState.Succeeded, EndedAtUtc = started.AddSeconds(8) },
                second with {
                    State = PlanItemState.Failed, StartedAtUtc = started.AddSeconds(8), EndedAtUtc = ended,
                    Error = reason, Result = result
                },
                third with { State = PlanItemState.Cancelled, EndedAtUtc = ended, Reason = "prior_item_failed" }
            ]
        };
        var terminal = worker with { RunState = RunState.Idle, ActiveRun = null, LastRun = failedRun };
        written.Clear();
        log.Observe(terminal);
        log.Observe(terminal);
        log.Observe(active);
        Require(written.Count == 4 && written[0].Message.StartsWith("→ 任务结束 [task=第一项] [state=Succeeded]")
            && written[1].Level == LogLevel.Error && written[1].Message.StartsWith(
                "→ 任务结束 [task=第二项] [state=Failed] [cost=90.0s] [code=MaaTaskFailed] "
                + "[message=MaaFramework task 终态为 Failed。] [result={\"maaJobStatus\":\"Failed\"}]")
            && written[2].Level == LogLevel.Info
            && written[2].Message == "→ 任务未执行 [task=第三项] [state=Cancelled] [reason=prior_item_failed]"
            && written[3].Level == LogLevel.Error && written[3].Message.Contains("Run 结束 [state=Failed]")
            && written[3].Message.Contains("[failedTask=第二项] [code=MaaTaskFailed]")
            && written[3].Message.Contains("[items=Succeeded:1,Failed:1,Cancelled:1]"),
            $"失败 Run 的日志不符，且迟到的旧 Snapshot 不应再写：{string.Join(" | ", written)}");

        // After a GUI restart or a reconnect, a Run that already ended is still written, once.
        written.Clear();
        var reconnected = new RunResultLog(logger);
        reconnected.Observe(terminal);
        reconnected.Observe(terminal);
        Require(written.Count(entry => entry.Message.StartsWith("Run 结束")) == 1 && written.Count == 4,
            "重连后应补写已结束的 Run，且只写一次。");

        // A stopped Run is an expected end: nothing is written as an error.
        written.Clear();
        var cancelled = running with {
            RunId = Guid.NewGuid(), State = RunState.Cancelled, EndedAtUtc = ended, Items = [
                Item("已停止", PlanItemState.Cancelled, started) with { EndedAtUtc = ended, Reason = "user_requested" }
            ]
        };
        log.Observe(worker with { RunState = RunState.Idle, ActiveRun = null, LastRun = cancelled });
        Require(written.Count == 2 && written.All(entry => entry.Level == LogLevel.Info)
            && written[0].Message.Contains("[state=Cancelled] [reason=user_requested]"),
            $"手动停止不应写成错误：{string.Join(" | ", written)}");
    }

    private static void VerifyTerminalNotifications()
    {
        var tracker = new RunTerminalNotificationTracker();
        var active = CreateNotificationSnapshot(RunState.Running);
        if (tracker.Observe(active) is not null) {
            throw new InvalidOperationException("活动 Run 不应通知。");
        }
        var terminal = FinishNotificationRun(active, RunState.Succeeded);
        var notification = tracker.Observe(terminal);
        if (notification is not { Title: "任务已完成", Message: "所有任务已执行完成。", Failed: false }
            || tracker.Observe(terminal) is not null) {
            throw new InvalidOperationException("成功 Run 应恰好通知一次。");
        }
        if (tracker.Observe(terminal with { SnapshotFresh = false }) is not null
            || tracker.Observe(terminal) is not null
            || new RunTerminalNotificationTracker().Observe(terminal) is not null) {
            throw new InvalidOperationException("重连和 GUI 重启不得重发历史通知。");
        }
        foreach (var state in new[] { RunState.Failed, RunState.Cancelled }) {
            tracker = new RunTerminalNotificationTracker();
            active = CreateNotificationSnapshot(RunState.Running);
            tracker.Observe(active);
            if (state == RunState.Cancelled) {
                tracker.Observe(active with {
                    WorkerSnapshot = active.WorkerSnapshot! with {
                        RunState = RunState.Stopping,
                        ActiveRun = active.WorkerSnapshot.ActiveRun! with { State = RunState.Stopping }
                    }
                });
            }
            terminal = FinishNotificationRun(active, state);
            notification = tracker.Observe(terminal);
            if (state == RunState.Failed
                ? notification is not { Title: "任务运行失败", Failed: true,
                    Message: "失败任务：日常任务。打开 NarutoAutoGUI 查看运行动态。" }
                : notification is not null) {
                throw new InvalidOperationException("失败通知必须解析 Plan 标签，取消不通知。");
            }
            if (tracker.Observe(terminal) is not null) {
                throw new InvalidOperationException("重复终态不能通知。");
            }
        }
        tracker = new RunTerminalNotificationTracker();
        active = CreateNotificationSnapshot(RunState.Running);
        tracker.Observe(active);
        terminal = FinishNotificationRun(active, RunState.Failed);
        if (tracker.Observe(active with {
                Observation = WorkerObservation.IpcDisconnected, SnapshotFresh = false
            }) is not null || tracker.Observe(terminal with { SnapshotFresh = false }) is not null
            || tracker.Observe(terminal) is not { Failed: true } || tracker.Observe(terminal) is not null) {
            throw new InvalidOperationException("断线时不能推测终态，恢复后应通知一次。");
        }
        tracker = new RunTerminalNotificationTracker();
        tracker.Observe(active);
        terminal = terminal with { WorkerSnapshot = terminal.WorkerSnapshot! with {
            LastRun = terminal.WorkerSnapshot.LastRun! with { Items = [] }
        } };
        if (tracker.Observe(terminal)?.Message != "任务运行失败。打开 NarutoAutoGUI 查看运行动态。") {
            throw new InvalidOperationException("缺少失败项时须使用简短后备文案。");
        }

        // A Worker that faults during an observed Active Run is announced once. One already Faulted when its Run is
        // first seen is not, since nothing was observed to change.
        foreach (var state in new[] { RunState.Stopping, RunState.Running }) {
            tracker = new RunTerminalNotificationTracker();
            active = CreateNotificationSnapshot(state);
            var faulted = active with {
                WorkerSnapshot = active.WorkerSnapshot! with { WorkerState = WorkerState.Faulted }
            };
            tracker.Observe(active);
            if (tracker.Observe(faulted) is not { Title: "任务未能确认结束", Failed: true,
                    Message: "MaaNOP 可能仍在运行。打开 NarutoAutoGUI 结束桌面分身以恢复。" }
                || tracker.Observe(faulted) is not null
                || tracker.Observe(faulted with { SnapshotFresh = false }) is not null
                || tracker.Observe(faulted) is not null) {
                throw new InvalidOperationException($"{state} 的 Run 期间 Worker 故障应恰好提醒一次。");
            }
            if (new RunTerminalNotificationTracker().Observe(faulted) is not null) {
                throw new InvalidOperationException("没有已观察的 Run 时 Worker 故障不应提醒。");
            }
        }
    }

    private static void VerifyDiagnosticPackage(AppLogger logger, string testDirectory)
    {
        var root = Path.Combine(testDirectory, "diagnostics");
        var application = Path.Combine(root, "application");
        var actualLogs = Path.Combine(root, "fallback-logs");
        Directory.CreateDirectory(actualLogs);
        Directory.CreateDirectory(Path.Combine(application, "logs"));
        File.WriteAllText(Path.Combine(actualLogs, "NarutoAutoGUI-recent.log"), "actual log");
        File.WriteAllText(Path.Combine(application, "logs", "NarutoAutoGUI-wrong.log"), "wrong directory");
        var destination = Path.Combine(root, "package.zip");
        var metadata = new DiagnosticMetadata(DateTime.UtcNow, "1.7.1", "MaaNOP", "2.4.0",
            "Windows", "X64", "ConnectedHidden", "Connected", true, "Ready", "1.7.1", 2);
        new DiagnosticPackageExporter(logger).Export(destination, application, actualLogs, metadata);
        using var archive = ZipFile.OpenRead(destination);
        if (archive.GetEntry("logs/NarutoAutoGUI-recent.log") is null
            || archive.GetEntry("logs/NarutoAutoGUI-wrong.log") is not null
            || archive.GetEntry("diagnostics.json") is null) {
            throw new InvalidOperationException("诊断包必须使用实际日志目录。");
        }
        archive.Dispose();
        VerifyDiagnosticEntries(destination, ["logs/NarutoAutoGUI-recent.log"],
            ["logs/updater.log", "debug/maafw.log"], []);
        File.Delete(Path.Combine(actualLogs, "NarutoAutoGUI-recent.log"));
        for (var index = 0; index < 7; index++) {
            var path = Path.Combine(actualLogs, $"NarutoAutoGUI-{index}.log");
            File.WriteAllText(path, $"GUI log {index}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-index));
        }
        string[] rotations = [
            .. Enumerable.Range(1, 7).Select(day => $"debug/maafw.bak.2026.09.{day:D2}-09.35.00.1.log"),
            "debug/maafw.bak.2026.09.21-09.35.00.12.log", "debug/maafw.bak.2026.09.21-09.35.00.123.log"
        ];
        string[] images = ["debug/vision/recognition.jpg", "debug/on_error/task.png",
            "debug/screencap/task.png", "debug/screencap/task.jpg", "debug/screencap/task.jpeg",
            "debug/screencap/nested/task.png"];
        string[] optional = ["logs/updater.log", "debug/maafw.log", .. rotations, .. images];
        string[] excluded = ["config/maanop-config.json", "cache/foo", "state/foo", "debug/screenshot.png",
            "resource/foo", "interface.json", "agent/foo", "python/foo", "debug/maafw.bak.notes.log",
            "debug/maafw.bak.2026.09.21-09.35.00.123.log.png", "debug/other.log", "debug/vision/config.json",
            "debug/vision/not-a-draw.png", "debug/on_error/not-an-error.jpg", "debug/screencap/not-supported.bmp",
            "debug/unknown/task.png", "debug/crash.dmp"];
        foreach (var name in optional.Concat(excluded)) {
            var path = Path.Combine(application, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
        }
        var exporter = new DiagnosticPackageExporter(logger);
        exporter.Export(destination, application, actualLogs, metadata);
        string[] expected = ["logs/NarutoAutoGUI-0.log", "logs/NarutoAutoGUI-1.log", "logs/NarutoAutoGUI-2.log",
            "logs/NarutoAutoGUI-3.log", "logs/NarutoAutoGUI-4.log", .. optional];
        VerifyDiagnosticEntries(destination, expected, [], []);
        using (var packed = ZipFile.OpenRead(destination)) {
            foreach (var image in images) {
                using var reader = new StreamReader(packed.GetEntry(image)!.Open());
                if (reader.ReadToEnd() != image) {
                    throw new InvalidOperationException("诊断图片必须按原始内容导出。");
                }
            }
        }
        File.Delete(Path.Combine(application, "logs", "updater.log"));
        using (var locked = new FileStream(Path.Combine(application, "debug", "maafw.log"), FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None)) {
            exporter.Export(destination, application, actualLogs, metadata);
            VerifyDiagnosticEntries(destination, expected.Except(["logs/updater.log", "debug/maafw.log"]).ToArray(),
                ["logs/updater.log"], ["debug/maafw.log"]);
        }
        // A live writer stays open and can append after export; no logger shutdown is needed.
        using (var liveLog = new FileStream(Path.Combine(actualLogs, "NarutoAutoGUI-0.log"), FileMode.Append,
                   FileAccess.Write, FileShare.ReadWrite)) {
            exporter.Export(destination, application, actualLogs, metadata);
            liveLog.WriteByte(10);
        }
        exporter.Export(destination, application, Path.Combine(application, "logs"), metadata);
        VerifyDiagnosticEntries(destination,
            ["logs/NarutoAutoGUI-wrong.log", "debug/maafw.log", .. rotations, .. images], ["logs/updater.log"], []);
        foreach (var name in new[] { rotations[0], images[0] }) {
            using var locked = new FileStream(Path.Combine(application, name), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            exporter.Export(destination, application, actualLogs, metadata);
            VerifyDiagnosticEntries(destination, expected.Except(["logs/updater.log", name]).ToArray(),
                ["logs/updater.log"], [name]);
        }
        VerifyDiagnosticFailures(exporter, root, application, actualLogs, metadata);
        foreach (var link in new[] { "debug", "debug/vision", "debug/vision/nested" }) {
            VerifyDiagnosticLinks(exporter, root, actualLogs, metadata, link);
        }
    }

    private static void VerifyDiagnosticLinks(DiagnosticPackageExporter exporter, string root,
        string actualLogs, DiagnosticMetadata metadata, string relativeLink)
    {
        var application = Path.Combine(root, "linked-application", relativeLink.Replace('/', '-'));
        var outside = Path.Combine(root, "outside-debug");
        Directory.CreateDirectory(Path.Combine(outside, "vision"));
        File.WriteAllText(Path.Combine(outside, "maafw.log"), "outside log");
        File.WriteAllText(Path.Combine(outside, "outside.jpg"), "outside image");
        File.WriteAllText(Path.Combine(outside, "vision", "outside.jpg"), "outside image");
        CreateJunction(Path.Combine(application, relativeLink), outside);
        var destination = Path.Combine(root, "linked.zip");
        exporter.Export(destination, application, actualLogs, metadata);
        using var archive = ZipFile.OpenRead(destination);
        if (archive.Entries.Any(entry => entry.FullName.StartsWith("debug/", StringComparison.Ordinal))) {
            throw new InvalidOperationException("诊断导出不能通过 debug 目录链接读取范围外文件。");
        }
        using var json = JsonDocument.Parse(archive.GetEntry("diagnostics.json")!.Open());
        if (!json.RootElement.GetProperty("skippedFiles").EnumerateArray()
                .Any(item => item.GetString() == relativeLink)) {
            throw new InvalidOperationException("跳过的 debug 目录链接必须记录到 metadata。");
        }
    }

    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException("无法创建目录链接测试 fixture。");
        }
    }

    private static void VerifyDiagnosticEntries(string path, string[] included, string[] missing, string[] skipped)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (!entries.Order().SequenceEqual(included.Append("diagnostics.json").Order())) {
            throw new InvalidOperationException("诊断包必须只包含最新五份 GUI 日志和白名单文件，且不得重复。");
        }
        using var stream = archive.GetEntry("diagnostics.json")!.Open();
        using var document = JsonDocument.Parse(stream);
        var json = document.RootElement;
        if (!json.GetProperty("includedFiles").EnumerateArray().Select(item => item.GetString())
                .Order().SequenceEqual(included.Order())
            || !json.GetProperty("missingOptionalFiles").EnumerateArray().Select(item => item.GetString())
                .Order().SequenceEqual(missing.Order())
            || !json.GetProperty("skippedFiles").EnumerateArray().Select(item => item.GetString())
                .Order().SequenceEqual(skipped.Order())
            || json.GetProperty("narutoAutoGuiVersion").GetString() != "1.7.1"
            || json.GetProperty("generatedAtUtc").GetDateTime().Kind != DateTimeKind.Utc
            || json.GetProperty("project").GetProperty("name").GetString() != "MaaNOP"
            || json.GetProperty("project").GetProperty("version").GetString() != "2.4.0"
            || json.GetProperty("system").GetProperty("processArchitecture").GetString() != "X64"
            || !json.GetProperty("runtime").GetProperty("snapshotFresh").GetBoolean()
            || json.GetProperty("runtime").GetProperty("protocolVersion").GetInt32() != 2) {
            throw new InvalidOperationException("诊断元信息或文件清单不正确。");
        }
        string[] allowed = ["generatedAtUtc", "narutoAutoGuiVersion", "project", "name", "version", "system",
            "osVersion", "processArchitecture", "runtime", "childSessionState", "workerObservation", "snapshotFresh",
            "workerState", "workerVersion", "protocolVersion", "includedFiles", "missingOptionalFiles", "skippedFiles"];
        AssertMetadataWhitelist(json);

        void AssertMetadataWhitelist(JsonElement element)
        {
            foreach (var property in element.EnumerateObject()) {
                if (!allowed.Contains(property.Name)) {
                    throw new InvalidOperationException($"诊断元信息出现非白名单字段：{property.Name}");
                }
                if (property.Value.ValueKind == JsonValueKind.Object) {
                    AssertMetadataWhitelist(property.Value);
                }
            }
        }
    }

    private static void VerifyDiagnosticFailures(DiagnosticPackageExporter exporter, string root,
        string application, string actualLogs, DiagnosticMetadata metadata)
    {
        var blocked = Path.Combine(root, "blocked.zip");
        Directory.CreateDirectory(blocked);
        foreach (var destination in new[] { Path.Combine(root, "missing", "package.zip"), blocked }) {
            var failed = false;
            try {
                exporter.Export(destination, application, actualLogs, metadata);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                failed = true;
            }
            if (!failed || File.Exists(destination)
                || Directory.GetFiles(root, ".NarutoAutoGUI-diagnostics-*").Length != 0) {
                throw new InvalidOperationException("创建或提交失败必须报告失败，且不遗留最终 ZIP 或临时文件。");
            }
        }
        var existing = Path.Combine(root, "existing.zip");
        File.WriteAllText(existing, "keep existing destination");
        using (var locked = new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            try {
                exporter.Export(existing, application, actualLogs, metadata);
                throw new InvalidOperationException("无法替换目标文件时不能报告导出成功。");
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                if (Directory.GetFiles(root, ".NarutoAutoGUI-diagnostics-*").Length != 0) {
                    throw new InvalidOperationException("目标文件被占用时须清理临时文件。");
                }
            }
        }
        if (File.ReadAllText(existing) != "keep existing destination") {
            throw new InvalidOperationException("提交失败不能损坏已有目标文件。");
        }
    }

    private static void VerifyDiagnosticSettings(AppLogger logger, string testDirectory)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "diagnostic-settings"), (window, directory) => {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            ClickOnboarding(window, "SettingsNavigationItem");
            var settingsView = (Views.SettingsView)window.FindName("SettingsView");
            var settings = ConfigurationDescendants(settingsView).OfType<ScrollViewer>().First();
            var button = SettingsButtonFor(window, "diagnostics.export");
            var action = SettingsActionFor(window, "diagnostics.export");
            CaptureSettings("top");
            foreach (var id in new[] {
                "framework-save-on-error", "framework-save-draw", "framework-debug-mode"
            }) {
                var toggle = SettingsVisualDescendants(settingsView).OfType<Wpf.Ui.Controls.ToggleSwitch>()
                    .Single(control => System.Windows.Automation.AutomationProperties.GetAutomationId(control) == id);
                toggle.BringIntoView();
                PumpOnboarding();
                AssertVisible(toggle);
            }
            CaptureSettings("framework");
            button.BringIntoView();
            PumpOnboarding();
            AssertVisible(button);
            var path = Path.Combine(directory, "diagnostics.zip");
            var method = typeof(Views.MainWindow).GetMethod("ExportDiagnosticsAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(window, [path])!;
            if (button.IsEnabled || action.Status != "正在导出…") {
                throw new InvalidOperationException("导出在后台执行期间须禁用按钮并显示状态。");
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) {
                PumpOnboarding();
            }
            if (!task.IsCompleted || !button.IsEnabled || action.Status != "诊断包已导出") {
                throw new InvalidOperationException("后台导出应完成并恢复按钮，Dispatcher 可继续处理消息。");
            }
            task.GetAwaiter().GetResult();
            using (var archive = ZipFile.OpenRead(path)) {
                if (archive.GetEntry("diagnostics.json") is null) {
                    throw new InvalidOperationException("Settings 导出结果必须是有效诊断 ZIP。");
                }
            }
            var updateAction = SettingsActionFor(window, "update.check");
            updateAction.Status = string.Join("\n", Enumerable.Repeat("更新检查失败，请稍后重试。", 12));
            PumpOnboarding();
            settings.ScrollToEnd();
            PumpOnboarding();
            if (settings.ScrollableHeight <= 0 || settings.VerticalOffset <= 0) {
                throw new InvalidOperationException("最小窗口中较长的 Settings 状态必须可以向下滚动。");
            }
            AssertVisible(SettingsButtonFor(window, "support.openAfdian"));
            AssertVisible(SettingsButtonFor(window, "onboarding.replay"));
            if (settings.ExtentWidth > settings.ViewportWidth + 1) {
                throw new InvalidOperationException("最小窗口的 Settings 不能横向溢出。");
            }
            CaptureSettings("bottom");

            void AssertVisible(FrameworkElement element)
            {
                var bounds = element.TransformToAncestor(settings).TransformBounds(new Rect(element.RenderSize));
                if (bounds.Top < 0 || bounds.Bottom > settings.ActualHeight
                    || bounds.Left < 0 || bounds.Right > settings.ActualWidth) {
                    throw new InvalidOperationException("最小窗口中滚动后的 Settings 按钮必须完整可见。");
                }
            }

            void CaptureSettings(string suffix)
            {
                var output = Environment.GetEnvironmentVariable("NARUTO_SUPPORT_QA_DIR");
                if (string.IsNullOrWhiteSpace(output)) {
                    return;
                }
                Directory.CreateDirectory(output);
                SaveScreenshot(window, Path.Combine(output, $"settings-{suffix}.png"));
            }
        });
    }

    // Renders the whole window, overlays included, for the optional visual QA captures.
    private static void SaveScreenshot(Views.MainWindow window, string path)
    {
        var root = (FrameworkElement)window.FindName("WindowOverlayRoot");
        var bitmap = new RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static WorkerCoordinatorSnapshot CreateNotificationSnapshot(RunState state)
    {
        var now = DateTime.UtcNow;
        var project = new ProjectProvenance("MaaNOP", "2.4.0", 2, "fixture");
        var item = new RunPlanItem(Guid.NewGuid(), "Task", "日常任务", "Entry",
            ProtocolJson.ToElement(new { }), ProtocolJson.ToElement(new { }));
        var plan = new RunPlan(1, now, project, "fixture", ProtocolJson.ToElement(new { }), [item]);
        var run = new RunSnapshot(Guid.NewGuid(), "fixture", state, now, now, null, null,
            item.PlanItemId, 0, plan, [], null, null);
        var check = new DependencyCheck(true, null, null);
        var dependencies = new DependencyStatus(now, "fixture", "fixture", check, check, check, check, check);
        var snapshot = new WorkerSnapshot(1, now, 1, Guid.NewGuid(), 1, 1, "1.7.1", 2, "fixture", project,
            WorkerState.Ready, null, dependencies, state, run, null, 0, 0);
        return new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, snapshot, "fixture");
    }

    private static WorkerCoordinatorSnapshot FinishNotificationRun(WorkerCoordinatorSnapshot active, RunState state)
    {
        var snapshot = active.WorkerSnapshot!;
        var run = snapshot.ActiveRun!;
        var item = run.Plan.Items[0];
        var itemState = state == RunState.Failed ? PlanItemState.Failed : PlanItemState.Succeeded;
        var terminal = run with {
            State = state, EndedAtUtc = DateTime.UtcNow, CurrentPlanItemId = null, CurrentPlanItemIndex = null,
            Items = [new PlanItemSnapshot(item.PlanItemId, item.TaskName, "ignored snapshot label", item.Entry,
                item.ResolvedOptions, item.PipelineOverride, itemState, run.StartedAtUtc, DateTime.UtcNow,
                null, null, null)]
        };
        return active with {
            WorkerSnapshot = snapshot with { RunState = RunState.Idle, ActiveRun = null, LastRun = terminal }
        };
    }
}
