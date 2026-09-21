using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
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
            ["logs/updater.log", "debug/maa.log", "debug/maa.bak.log", "debug/maafw.log"], []);
        File.Delete(Path.Combine(actualLogs, "NarutoAutoGUI-recent.log"));
        for (var index = 0; index < 7; index++) {
            var path = Path.Combine(actualLogs, $"NarutoAutoGUI-{index}.log");
            File.WriteAllText(path, $"GUI log {index}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-index));
        }
        string[] optional = ["logs/updater.log", "debug/maa.log", "debug/maa.bak.log", "debug/maafw.log"];
        string[] excluded = ["config/maanop-config.json", "cache/foo", "state/foo", "debug/screenshot.png",
            "resource/foo", "interface.json", "agent/foo", "python/foo"];
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
        File.Delete(Path.Combine(application, "debug", "maa.bak.log"));
        using (var locked = new FileStream(Path.Combine(application, "debug", "maa.log"), FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None)) {
            exporter.Export(destination, application, actualLogs, metadata);
            VerifyDiagnosticEntries(destination, expected.Except(["debug/maa.log", "debug/maa.bak.log"]).ToArray(),
                ["debug/maa.bak.log"], ["debug/maa.log"]);
        }
        // A live writer stays open and can append after export; no logger shutdown is needed.
        using (var liveLog = new FileStream(Path.Combine(actualLogs, "NarutoAutoGUI-0.log"), FileMode.Append,
                   FileAccess.Write, FileShare.ReadWrite)) {
            exporter.Export(destination, application, actualLogs, metadata);
            liveLog.WriteByte(10);
        }
        exporter.Export(destination, application, Path.Combine(application, "logs"), metadata);
        VerifyDiagnosticEntries(destination,
            ["logs/NarutoAutoGUI-wrong.log", "logs/updater.log", "debug/maa.log", "debug/maafw.log"],
            ["debug/maa.bak.log"], []);
        VerifyDiagnosticFailures(exporter, root, application, actualLogs, metadata);
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
            var settings = (ScrollViewer)window.FindName("SettingsView");
            var button = (System.Windows.Controls.Button)window.FindName("ExportDiagnosticsButton");
            var status = (TextBlock)window.FindName("DiagnosticsStatusText");
            CaptureSettings("top");
            button.BringIntoView();
            PumpOnboarding();
            AssertVisible(button);
            var path = Path.Combine(directory, "diagnostics.zip");
            var method = typeof(Views.MainWindow).GetMethod("ExportDiagnosticsAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(window, [path])!;
            if (button.IsEnabled || status.Text != "正在导出…") {
                throw new InvalidOperationException("导出在后台执行期间须禁用按钮并显示状态。");
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) {
                PumpOnboarding();
            }
            if (!task.IsCompleted || !button.IsEnabled || status.Text != "诊断包已导出") {
                throw new InvalidOperationException("后台导出应完成并恢复按钮，Dispatcher 可继续处理消息。");
            }
            task.GetAwaiter().GetResult();
            using (var archive = ZipFile.OpenRead(path)) {
                if (archive.GetEntry("diagnostics.json") is null) {
                    throw new InvalidOperationException("Settings 导出结果必须是有效诊断 ZIP。");
                }
            }
            settings.ScrollToEnd();
            PumpOnboarding();
            AssertVisible(button);
            AssertVisible((FrameworkElement)window.FindName("ReplayOnboardingButton"));
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
                var root = (FrameworkElement)window.FindName("WindowOverlayRoot");
                var bitmap = new RenderTargetBitmap(
                    (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"settings-{suffix}.png"));
                encoder.Save(file);
            }
        });
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
