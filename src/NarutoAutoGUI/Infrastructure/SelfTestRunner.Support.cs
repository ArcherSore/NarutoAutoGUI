using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Views;
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
        VerifyLogRolling(Path.Combine(testDirectory, "log-rolling"));
    }

    // Past the size limit a log rolls to a name the diagnostic package still matches.
    private static void VerifyLogRolling(string directory)
    {
        using var logger = new AppLogger(directory);
        var megabyte = new string('x', 1024 * 1024);
        for (var index = 0; index < 11; index++) {
            logger.Debug(megabyte);
        }
        Require(Directory.GetFiles(directory, "NarutoAutoGUI-*_001.log").Length == 1,
            "单个日志文件超过 10 MB 时应滚动到新文件。");
    }

    private static void VerifyFileLoggingInCurrentCulture(string directory)
    {
        Directory.CreateDirectory(directory);
        string LogPath(int daysAgo) => Path.Combine(directory, "NarutoAutoGUI-"
            + DateTime.Now.AddDays(-daysAgo).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        // Thirteen older files and today's make the fourteen that stay, however old those thirteen are.
        var previous = Enumerable.Range(1, 15).Select(daysAgo => LogPath(daysAgo * 30)).ToArray();
        foreach (var path in previous) {
            File.WriteAllText(path, "previous");
        }

        var logger = new AppLogger(directory);
        var written = new List<LogEntry>();
        logger.EntryWritten += (_, entry) => written.Add(entry);
        logger.Info("""花括号 {"key":"{value}"} {0} {Text}""");
        logger.Error("失败", new InvalidOperationException("file-logging-exception"));
        // Relayed logs keep the Worker's time; one from another day, recovered after a restart, also shows its date.
        var today = DateTime.Today;
        // Noon, as a daylight-saving change can skip 01:02 on the day this runs.
        var relayedAt = new DateTime(today.Year, today.Month, today.Day, 12, 2, 3, 4, DateTimeKind.Local);
        logger.WriteWorker(LogLevel.Warn, "#1 [run.lifecycle] 转写", new DateTimeOffset(relayedAt));
        MainWindow.WriteWorkerDiagnosticLog(logger, new WorkerLogEntry(
            2, relayedAt.AddDays(-2).ToUniversalTime(), "INFO", "run.lifecycle", "回放", false, null, null, null, null));
        // Agent output keeps its terminal colours and pretask output its line break; neither belongs in the log.
        MainWindow.WriteWorkerDiagnosticLog(logger, new WorkerLogEntry(3, relayedAt.ToUniversalTime(), "INFO",
            "agent.stdout", "\u001b[31m[ERR] 内容\u001b[0m\r\n", false, null, null, null, null));
        logger.Info("末尾空白。 ");
        App.LogStartup(logger);
        using (logger.Scope("作用域")) {
        }
        var current = Directory.GetFiles(directory, "NarutoAutoGUI-*.log").Except(previous).Single();
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
            || !Regex.IsMatch(text, @"^\[12:02:03\.004\] \[WARN\] \[T\d+\] \[Worker\] "
                + @"#1 \[run\.lifecycle\] 转写\r?$", RegexOptions.Multiline)
            || !Regex.IsMatch(text, @"\[Worker\] #3 \[agent\.stdout\] \[ERR\] 内容\r?$", RegexOptions.Multiline)
            || !Regex.IsMatch(text, @"\] 末尾空白。\r?$", RegexOptions.Multiline)
            || text.Contains("\n\r\n")
            || !text.Contains($"[{relayedAt.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
                + "12:02:03.004] [INFO]")
            || !text.Contains("[Worker] #2 [run.lifecycle] 回放")
            || !Regex.IsMatch(text, @"\[INFO\] \[T\d+\] \[[^\]]+\] 作用域 \| enter\r?\n.*\] 作用域 \| leave, \d+ms\r?$",
                RegexOptions.Multiline)
            || !Regex.IsMatch(text, @"\[App\.LogStartup:\d+\] NarutoAutoGUI 正式 GUI 启动 \[version=\S+\] "
                + $@"\[pid={Environment.ProcessId}\] \[session=\d+\] \[elevated=(True|False)\] "
                + @".*\[logDirectory=.*\[args=")) {
            throw new InvalidOperationException($"日志行格式不符：{text}");
        }
        if (Directory.GetFiles(directory, "NarutoAutoGUI-*.log").Length != 14
            || !previous.Take(13).All(File.Exists) || previous.Skip(13).Any(File.Exists)) {
            throw new InvalidOperationException("日志应保留最新的 14 个文件，不论它们有多旧。");
        }

        logger.Dispose();
        var count = written.Count;
        logger.Info("释放后不再写入");
        if (written.Count != count
            || Directory.GetFiles(directory).Any(path => File.ReadAllText(path).Contains("释放后不再写入"))) {
            throw new InvalidOperationException("释放后写日志应被安全忽略。");
        }
    }

    private static void VerifyUserProfileMasking(string testDirectory)
    {
        const string sample = @"C:\Users\Alice";
        foreach (var (input, expected) in new[] {
            (@"C:\Users\Alice\AppData", @"%USERPROFILE%\AppData"),
            (@"路径 c:\users\ALICE\x。", @"路径 %USERPROFILE%\x。"),
            (@"C:/Users/Alice/x", "%USERPROFILE%/x"),
            (@"{""path"":""C:\\Users\\Alice\\x""}", @"{""path"":""%USERPROFILE%\\x""}"),
            (@"目录是 C:\Users\Alice。", "目录是 %USERPROFILE%。"),
            (@"目录是 C:\Users\Alice.", "目录是 %USERPROFILE%."),
            (@"C:\Users\Alice2\x", @"C:\Users\Alice2\x"),
            (@"C:\Users\Alice.old\x", @"C:\Users\Alice.old\x"),
            (@"D:\Program Files\x", @"D:\Program Files\x")
        }) {
            var masked = AppLogger.MaskUserProfile(input, sample);
            Require(masked == expected, $"用户目录替换不符：{input} -> {masked}");
        }
        Require(AppLogger.MaskUserProfile(sample, string.Empty) == sample, "取不到用户目录时不应改动日志。");

        // Whichever way a path reaches the log, the file and the entries raised for the UI both hide the real folder.
        var directory = Path.Combine(testDirectory, "user-profile-log");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(profile, "AppData", "x.exe");
        var written = new List<LogEntry>();
        using (var logger = new AppLogger(directory)) {
            logger.EntryWritten += (_, entry) => written.Add(entry);
            logger.Info($"消息 {path}");
            logger.Error($"异常 {path}", new IOException($"异常内容 {path}"));
            logger.Warn(System.Text.Json.JsonSerializer.Serialize(new { path }));
            logger.WriteWorker(LogLevel.Info, $"Worker 转写 {path}", DateTimeOffset.Now);
        }
        var text = File.ReadAllText(Directory.GetFiles(directory, "NarutoAutoGUI-*.log").Single());
        Require(!text.Contains(profile, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(profile.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase)
            && written.All(entry => !entry.Message.Contains(profile, StringComparison.OrdinalIgnoreCase)),
            $"日志不应包含真实用户目录：{text}");
        Require(Regex.Matches(text, @"%USERPROFILE%\\AppData\\x\.exe").Count == 4
            && text.Contains(@"%USERPROFILE%\\AppData\\x.exe")
            && text.Contains(@"System.IO.IOException: 异常内容 %USERPROFILE%\AppData\x.exe"),
            $"路径应替换为 %USERPROFILE% 并保留后续部分：{text}");
    }

    private static void VerifyWorkerStateLog(string testDirectory)
    {
        using var logger = new AppLogger(Path.Combine(testDirectory, "worker-state-log"));
        var written = new List<LogEntry>();
        logger.EntryWritten += (_, entry) => written.Add(entry);
        var log = new WorkerStateLog(logger);
        var connected = CreateNotificationSnapshot(RunState.Running);
        var worker = connected.WorkerSnapshot!;
        var idle = connected with {
            WorkerSnapshot = worker with { RunState = RunState.Idle, ActiveRun = null }, Detail = "Worker Snapshot 已同步"
        };
        void Expect(WorkerCoordinatorSnapshot snapshot, string failure, params (LogLevel Level, string Text)[] expected)
        {
            written.Clear();
            log.Observe(() => snapshot);
            log.Observe(() => snapshot);
            Require(written.Count == expected.Length && written.Zip(expected).All(
                pair => pair.First.Level == pair.Second.Level && pair.First.Message.Contains(pair.Second.Text)),
                $"{failure}：{string.Join(" | ", written)}");
        }

        Expect(WorkerCoordinatorSnapshot.Empty, "启动时应记录初始观察",
            (LogLevel.Info, "Worker 观察变化 [to=WorkerNotStarted] [detail=Worker 尚未启动]"));
        Expect(idle, "连接后应记录观察和 Worker 状态",
            (LogLevel.Info, "[from=WorkerNotStarted] [to=Connected]"), (LogLevel.Info, "Worker 状态变化 [to=Ready]"));
        Expect(idle with { Detail = "Worker 状态已更新" }, "连接期间的 Detail 变化不应写日志");

        // Ending the Runtime Environment disconnects an idle Worker on purpose: this is not a warning.
        Expect(idle with { Observation = WorkerObservation.IpcDisconnected, SnapshotFresh = false, Detail = "断开" },
            "空闲 Worker 断开不应是警告", (LogLevel.Info, "[from=Connected] [to=IpcDisconnected] [detail=断开]"));
        Expect(idle, "重新连接应写日志", (LogLevel.Info, "[from=IpcDisconnected] [to=Connected]"));

        // Losing a Worker mid-Run, then proving it gone, is what a killed Worker looks like in the log.
        var running = connected with { Detail = "Worker 状态已更新" };
        Expect(running, "同状态的 Run 变化不应写 Worker 状态");
        var lost = running with { SnapshotFresh = false };
        Expect(lost with { Observation = WorkerObservation.IpcDisconnected, Detail = "Worker 仍存活，IPC 已断开" },
            "Run 期间断开应警告", (LogLevel.Warn, "[to=IpcDisconnected] [detail=Worker 仍存活，IPC 已断开]"));
        Expect(lost with { Observation = WorkerObservation.WorkerExited, Detail = "Worker 进程已退出" },
            "Run 期间 Worker 退出应警告",
            (LogLevel.Warn, "[from=IpcDisconnected] [to=WorkerExited] [detail=Worker 进程已退出]"));
        Expect(lost with { Observation = WorkerObservation.WorkerExited, Detail = "详情变化" },
            "观察没变、只有详情变化时不应写 from", (LogLevel.Warn, "Worker 观察变化 [to=WorkerExited] [detail=详情变化]"));
        Expect(lost with {
            Observation = WorkerObservation.WorkerRecoveryConflict, WorkerSnapshot = null, Detail = "无法确认"
        }, "恢复冲突总是警告", (LogLevel.Warn, "[to=WorkerRecoveryConflict]"));

        // A fault keeps the Run active; its reason reaches the log even though no Run ended.
        var faulted = running with { WorkerSnapshot = worker with {
            WorkerState = WorkerState.Faulted, WorkerReason = new StructuredReason("StopTimeout", "Stop 未确认。 ")
        } };
        Expect(faulted, "Faulted 应带原因写成错误", (LogLevel.Info, "[to=Connected]"),
            (LogLevel.Error, "Worker 状态变化 [from=Ready] [to=Faulted] [code=StopTimeout] [message=Stop 未确认。]"));

        // A replacement Worker has no previous state to come from.
        Expect(idle with { WorkerSnapshot = idle.WorkerSnapshot! with { WorkerInstanceId = Guid.NewGuid() } },
            "新 Worker 不应沿用旧状态", (LogLevel.Info, "Worker 状态变化 [to=Ready]"));
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

        // The plan is written whole, its options as the Run will use them, and long options cut with their length.
        written.Clear();
        var plan = running.Plan;
        var longOption = ProtocolJson.ToElement(new { text = new string('x', 3000) });
        RunResultLog.WritePlan(logger, new NarutoAutoGUI.ProjectModel.RunStartAttempt(
            running.RunId, plan with { Items = [
                plan.Items[0] with { ResolvedOptions = ProtocolJson.ToElement(new { server = "公测1006" }) },
                plan.Items[0] with { ResolvedOptions = longOption, Kind = PlanItemKind.Pretask }
            ] }, "digest"), "配置 1", startupOnly: false);
        Require(written.Count == 3 && written[0].Message.StartsWith($"Run 计划 [configuration=配置 1] "
                + $"[startupOnly=False] [runId={running.RunId}] [planDigest=digest] [items=2]")
            && written[1].Message == "→ 计划项 [index=1/2] [task=日常任务] [name=Task] [kind=MaaTask] [entry=Entry] "
                + "[options={\"server\":\"公测1006\"}]"
            && written[2].Message.Contains("[kind=Pretask]") && written[2].Message.EndsWith("…（共 3011 字符）]"),
            $"Run 计划日志不符：{string.Join(" | ", written)}");

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
        var now = new DateTime(2026, 11, 7, 12, 0, 0);
        var exporter = new DiagnosticPackageExporter(logger);
        Directory.CreateDirectory(actualLogs);
        string Create(string relative, string? text = null, int ageMinutes = 0, string? under = null) => WriteFixture(
            under ?? application, relative, System.Text.Encoding.UTF8.GetBytes(text ?? relative), ageMinutes);

        Create("NarutoAutoGUI-recent.log", "actual log", under: actualLogs);
        Create("logs/NarutoAutoGUI-wrong.log", "wrong directory");
        var first = exporter.Export(application, actualLogs, "MaaNOP", "2.4.0", now);
        var entries = VolumeEntries(first.FirstVolume);
        Require(entries.Contains("logs/NarutoAutoGUI-recent.log") && !entries.Contains("logs/NarutoAutoGUI-wrong.log"),
            "诊断包必须使用实际日志目录。");
        Require(!entries.Contains("diagnostics.json"), "诊断包不再生成清单。");
        Require(first.Volumes == 1 && first.Files == entries.Length, "导出结果应如实报告卷数和文件数。");
        Require(Path.GetFileName(first.FirstVolume)
            .StartsWith("MaaNOP-logs-2.4.0-20261107-120000", StringComparison.Ordinal),
            "目录应带项目、版本和时间，便于反馈时识别。");

        string[] included = [
            Create("logs/NarutoAutoWorker.bootstrap.log"),
            Create("logs/updater.log"), Create("debug/maafw.log"), Create("config/maanop-config.json"),
            Create("config/maa-save-on-error.txt"), Create("debug/on_error/new.png", ageMinutes: 1),
            Create("debug/on_error/old.png", ageMinutes: 90), Create("debug/vision/draw.jpg"),
            Create("debug/screencap/frame.jpg"), Create("debug/maafw.bak.2026.09.21-09.35.00.123.log")
        ];
        string[] excluded = [
            Create("config/other.json"), Create("config/clear-on-startup.txt"), Create("cache/foo"),
            Create("state/foo"), Create("resource/foo"), Create("interface.json"), Create("debug/screenshot.png"),
            Create("debug/other.log"),
            Create("debug/maafw.bak.notes.log"), Create("debug/vision/not-a-draw.png"),
            Create("debug/on_error/not-an-error.jpg"), Create("debug/unknown/task.png"), Create("debug/crash.dmp")
        ];
        var relative = (string path) => MaaDebugImages.RelativePath(application, path);
        var complete = exporter.Export(application, actualLogs, "MaaNOP", null, now.AddSeconds(1));
        entries = VolumeEntries(complete.FirstVolume);
        foreach (var path in included) {
            Require(entries.Contains(relative(path)), $"诊断包应包含：{relative(path)}");
        }
        Require(entries[0] == "logs/NarutoAutoWorker.bootstrap.log", "启动失败日志不能被大量旧日志挤到后面的分卷。");
        foreach (var path in excluded) {
            Require(!entries.Contains(relative(path)), $"诊断包不应包含：{relative(path)}");
        }
        using (var archive = ZipFile.OpenRead(complete.FirstVolume)) {
            using var reader = new StreamReader(archive.GetEntry("debug/on_error/new.png")!.Open());
            Require(reader.ReadToEnd() == "debug/on_error/new.png", "诊断图片必须按原始内容导出。");
            Require(archive.GetEntry("debug/on_error/new.png")!.CompressedLength
                == archive.GetEntry("debug/on_error/new.png")!.Length, "图片已压缩过，不应再压缩一次。");
        }

        VerifyDiagnosticVolumes(exporter, Path.Combine(root, "volumes"), now);
        VerifyDiagnosticBudgets(exporter, Path.Combine(root, "budgets"), now);
        VerifyDiagnosticMasking(exporter, Path.Combine(root, "masking"), now);
        VerifyDiagnosticRetention(exporter, Path.Combine(root, "retention"), now);
        VerifyDiagnosticFailures(exporter, Path.Combine(root, "failures"), now);

        // A live writer stays open and can append after export; no logger shutdown is needed.
        using (var liveLog = new FileStream(Path.Combine(actualLogs, "NarutoAutoGUI-recent.log"), FileMode.Append,
                   FileAccess.Write, FileShare.ReadWrite)) {
            exporter.Export(application, actualLogs, "MaaNOP", null, now.AddSeconds(2));
            liveLog.WriteByte(10);
        }
        foreach (var name in new[] { "debug/maafw.log", "debug/vision/draw.jpg" }) {
            using var locked = new FileStream(Path.Combine(application, name), FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
            var result = exporter.Export(application, actualLogs, "MaaNOP", null, now.AddSeconds(3));
            entries = VolumeEntries(result.FirstVolume);
            Require(result.Skipped == 1 && !entries.Contains(name) && entries.Contains("logs/updater.log"),
                "无法读取的文件应跳过并计数，其余文件照常导出。");
        }
        foreach (var link in new[] { "debug", "debug/vision", "debug/vision/nested" }) {
            VerifyDiagnosticLinks(exporter, root, actualLogs, now, link);
        }
    }

    private static string WriteFixture(string root, string relative, byte[] content, int ageMinutes = 0)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
        return path;
    }

    private static string[] VolumeEntries(string volume)
    {
        using var archive = ZipFile.OpenRead(volume);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static string[] ExportVolumes(string firstVolume) =>
        Directory.GetFiles(Path.GetDirectoryName(firstVolume)!, "*.zip").Order(StringComparer.Ordinal).ToArray();

    // Each volume must stay under the limit and open alone, with the newest failure in the one people send first.
    private static void VerifyDiagnosticVolumes(DiagnosticPackageExporter exporter, string root, DateTime now)
    {
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "NarutoAutoGUI-current.log"), "current log");
        var random = new Random(1);
        void Image(string relative, int minutes)
        {
            var bytes = new byte[3000];
            random.NextBytes(bytes);
            WriteFixture(root, relative, bytes, minutes);
        }
        for (var minutes = 1; minutes <= 6; minutes++) {
            Image($"debug/on_error/error-{minutes}.png", minutes);
            Image($"debug/vision/draw-{minutes}.jpg", minutes);
        }
        var limits = new DiagnosticExportLimits(VolumeBytes: 10_000);
        var result = exporter.Export(root, logs, null, null, now, limits);
        var volumes = ExportVolumes(result.FirstVolume);
        Require(result.Volumes == volumes.Length && volumes.Length > 1, "超过单卷上限时应分卷。");
        Require(volumes[0] == result.FirstVolume, "定位的应是第一卷。");
        Require(volumes.All(volume => new FileInfo(volume).Length <= limits.VolumeBytes), "每卷都不能超过上限。");
        var all = volumes.SelectMany(VolumeEntries).ToArray();
        Require(all.Length == 13 && all.Distinct().Count() == 13, "所有文件都应进入分卷，且不能重复。");
        var firstEntries = VolumeEntries(volumes[0]);
        Require(firstEntries.Contains("logs/NarutoAutoGUI-current.log")
            && firstEntries.Contains("debug/on_error/error-1.png"), "第一卷应包含当前日志和最新的失败截图。");
        var order = all.ToList();
        Require(order.FindLastIndex(entry => entry.Contains("on_error"))
            < order.FindIndex(entry => entry.Contains("vision")), "失败截图应排在标注图之前。");

        var oversized = Path.Combine(root, "debug", "on_error", "oversized.png");
        var bytes = new byte[30_000];
        random.NextBytes(bytes);
        File.WriteAllBytes(oversized, bytes);
        result = exporter.Export(root, logs, null, null, now.AddSeconds(1), limits);
        Require(ExportVolumes(result.FirstVolume).SelectMany(VolumeEntries).Contains("debug/on_error/oversized.png"),
            "超过单卷上限的文件也要导出，独占一卷。");
    }

    private static void VerifyDiagnosticBudgets(DiagnosticPackageExporter exporter, string root, DateTime now)
    {
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        void Write(string relative, int minutes) => WriteFixture(root, relative, new byte[100], minutes);
        Write("debug/maafw.log", 0);
        for (var minutes = 1; minutes <= 4; minutes++) {
            Write($"debug/maafw.bak.2026.09.0{minutes}-09.35.00.1.log", minutes);
            Write($"debug/vision/draw-{minutes}.jpg", minutes);
            Write($"debug/on_error/error-{minutes}.png", minutes);
        }
        var result = exporter.Export(root, logs, null, null, now,
            new DiagnosticExportLimits(FrameworkLogBytes: 300, DrawImageBytes: 200));
        var entries = ExportVolumes(result.FirstVolume).SelectMany(VolumeEntries).ToArray();
        Require(entries.Count(entry => entry.Contains("maafw.bak")) == 2
            && entries.Contains("debug/maafw.bak.2026.09.01-09.35.00.1.log")
            && entries.Contains("debug/maafw.bak.2026.09.02-09.35.00.1.log"), "框架日志应从新到旧取，不超过预算。");
        Require(entries.Count(entry => entry.Contains("/vision/")) == 2 && entries.Contains("debug/vision/draw-1.jpg")
            && entries.Contains("debug/vision/draw-2.jpg"), "标注图应从新到旧取，不超过预算。");
        Require(entries.Count(entry => entry.Contains("/on_error/")) == 4, "失败截图不受标注图预算限制。");
    }

    // Native logs do not pass through AppLogger, so the export is the last chance to keep the account name out.
    private static void VerifyDiagnosticMasking(DiagnosticPackageExporter exporter, string root, DateTime now)
    {
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(root, "debug", "maafw.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"Working {AppLogger.UserProfile}\\game\nnext line\n");
        var result = exporter.Export(root, logs, null, null, now);
        using var archive = ZipFile.OpenRead(result.FirstVolume);
        using var reader = new StreamReader(archive.GetEntry("debug/maafw.log")!.Open());
        var text = reader.ReadToEnd();
        Require(text.Contains("%USERPROFILE%\\game") && text.Contains("next line")
            && !text.Contains(AppLogger.UserProfile, StringComparison.OrdinalIgnoreCase), "文本日志应掩码用户目录。");
    }

    private static void VerifyDiagnosticRetention(DiagnosticPackageExporter exporter, string root, DateTime now)
    {
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var unrelated = Path.Combine(root, "debug", "exports", "keep-me");
        Directory.CreateDirectory(unrelated);
        var results = Enumerable.Range(0, 12)
            .Select(index => exporter.Export(root, logs, null, null, now.AddMinutes(index))).ToArray();
        var kept = Directory.GetDirectories(Path.Combine(root, "debug", "exports")).Except([unrelated]).ToArray();
        Require(kept.Length == 10, $"应保留最近 10 次导出：{kept.Length}");
        Require(results.TakeLast(10).All(result => File.Exists(result.FirstVolume))
            && results.Take(2).All(result => !File.Exists(result.FirstVolume)), "清理的应是最旧的导出。");
        Require(Directory.Exists(unrelated), "不是导出目录的文件夹不得删除。");
    }

    private static void VerifyDiagnosticLinks(DiagnosticPackageExporter exporter, string root, string actualLogs,
        DateTime now, string relativeLink)
    {
        var application = Path.Combine(root, "linked-application", relativeLink.Replace('/', '-'));
        var outside = Path.Combine(root, "outside-debug", relativeLink.Replace('/', '-'));
        Directory.CreateDirectory(Path.Combine(outside, "vision"));
        File.WriteAllText(Path.Combine(outside, "maafw.log"), "outside log");
        File.WriteAllText(Path.Combine(outside, "outside.jpg"), "outside image");
        File.WriteAllText(Path.Combine(outside, "vision", "outside.jpg"), "outside image");
        CreateJunction(Path.Combine(application, relativeLink), outside);
        var result = exporter.Export(application, actualLogs, null, null, now);
        Require(result.Skipped > 0 && !VolumeEntries(result.FirstVolume).Any(entry =>
            entry.StartsWith("debug/", StringComparison.Ordinal)), "诊断导出不能通过目录链接读取范围外文件。");
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
            throw new SelfTestSkip("临时目录所在文件系统无法创建目录链接，未验证诊断包拒绝链接。");
        }
    }

    private static void VerifyDiagnosticFailures(DiagnosticPackageExporter exporter, string root, DateTime now)
    {
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.Combine(root, "debug"));
        // A file where the exports directory belongs makes the export itself impossible.
        File.WriteAllText(Path.Combine(root, "debug", "exports"), "blocks the exports directory");
        Require(Throws<IOException>(() => exporter.Export(root, logs, null, null, now)), "无法创建导出目录时必须报告失败。");
        File.Delete(Path.Combine(root, "debug", "exports"));

        // The second export of the same second replaces the first, and an open target must not lose earlier work.
        var result = exporter.Export(root, logs, null, null, now);
        var retry = exporter.Export(root, logs, null, null, now);
        Require(retry.FirstVolume == result.FirstVolume && File.Exists(retry.FirstVolume), "重复导出应得到完整结果。");
        var finished = retry.FirstVolume;
        using var locked = new FileStream(finished, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(Throws<IOException>(() => exporter.Export(root, logs, null, null, now)) && File.Exists(finished),
            "目标被占用时必须报告失败，且不能损坏已有的包。");
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
            string? revealed = null;
            Action<string> reveal = file => revealed = file;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var method = typeof(Views.MainWindow).GetMethod("ExportDiagnosticsAsync", flags)!;
            var clear = SettingsActionFor(window, "diagnostics.clear");
            clear.Status = "";
            var task = (Task)method.Invoke(window, [reveal])!;
            if (button.IsEnabled || string.IsNullOrEmpty(action.Status)) {
                throw new InvalidOperationException("导出在后台执行期间须禁用按钮并显示状态。");
            }
            // A clean during a multi-volume export would delete the finished volumes of a package reported as complete.
            Require(!clear.IsEnabled, "导出期间不能开始清理。");
            void Await(Task pending)
            {
                PumpUntil(() => pending.IsCompleted, "后台任务应完成，Dispatcher 可继续处理消息。");
                pending.GetAwaiter().GetResult();
            }
            Await(task);
            Require(button.IsEnabled && clear.IsEnabled, "导出完成后应恢复按钮。");
            PumpUntil(() => !string.IsNullOrEmpty(clear.Status), "导出后应刷新可清理的大小。");
            Require(revealed is not null && revealed.StartsWith(
                Path.Combine(directory, "debug", "exports"), StringComparison.OrdinalIgnoreCase),
                "一键导出应写到 debug\\exports，并定位到第一卷。");
            using (var archive = ZipFile.OpenRead(revealed)) {
                Require(archive.Entries.Count > 0, "Settings 导出结果必须是有效诊断 ZIP。");
            }

            var clearMethod = typeof(Views.MainWindow).GetMethod("ClearDebugFilesAsync", flags)!;
            // Whether to clear during a Run is the user's decision.
            var coordinator = typeof(Views.MainWindow).GetField("_workerCoordinator", flags)!.GetValue(window)!;
            var coordinatorSnapshot = coordinator.GetType().GetProperty("Snapshot", flags)!;
            var observed = coordinatorSnapshot.GetValue(coordinator);
            coordinatorSnapshot.SetValue(coordinator, CreateNotificationSnapshot(RunState.Running));
            var clearing = (Task)clearMethod.Invoke(window, [])!;
            Require(!action.IsEnabled, "清理期间不能开始导出。");
            Await(clearing);
            coordinatorSnapshot.SetValue(coordinator, observed);
            Require(!File.Exists(revealed) && clear.IsEnabled && action.IsEnabled,
                "任务运行中点击也应直接清理文件并恢复按钮。");
            var updateAction = SettingsActionFor(window, "update.check");
            updateAction.Status = string.Join("\n", Enumerable.Repeat("更新检查失败，请稍后重试。", 12));
            PumpOnboarding();
            settings.ScrollToEnd();
            PumpOnboarding();
            if (settings.ScrollableHeight <= 0 || settings.VerticalOffset <= 0) {
                throw new InvalidOperationException("最小窗口中较长的 Settings 状态必须可以向下滚动。");
            }
            AssertVisible(SettingsButtonFor(window, "support.openAfdian"));
            var replay = SettingsButtonFor(window, "onboarding.replay");
            replay.BringIntoView();
            PumpOnboarding();
            AssertVisible(replay);
            settings.ScrollToEnd();
            PumpOnboarding();
            if (settings.ExtentWidth > settings.ViewportWidth + 1) {
                throw new InvalidOperationException("最小窗口的 Settings 不能横向溢出。");
            }
            CaptureSettings("bottom");

            void AssertVisible(FrameworkElement element)
            {
                var bounds = element.TransformToAncestor(settings).TransformBounds(new Rect(element.RenderSize));
                if (bounds.Top < -PixelTolerance || bounds.Bottom > settings.ActualHeight + PixelTolerance
                    || bounds.Left < -PixelTolerance || bounds.Right > settings.ActualWidth + PixelTolerance) {
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
        var dependencies = new DependencyStatus(now, "fixture", "fixture", check, check, check);
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
