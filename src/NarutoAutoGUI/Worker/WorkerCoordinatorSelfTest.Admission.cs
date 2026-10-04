using System.ComponentModel;
using System.Diagnostics;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static partial class WorkerCoordinatorSelfTest
{
    private const string EndChildSessionText = "结束桌面分身";

    // Stale Admission recovery: delete only with proof, fail closed otherwise, never launch into an occupied session.
    private static async Task VerifyAdmissionRecoveryAsync(
        AppLogger logger, string testDirectory, string workerPath, uint session, ProjectPlanModule project)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var context = new AdmissionContext(logger, Path.Combine(testDirectory, "admission"), workerPath, session,
            project, FindAbsentSession(), timeout.Token);
        await VerifyDeadPidRecoveryAsync(context);
        await VerifyWorkerExitAfterStartupAsync(context);
        await VerifyReusedPidRecoveryAsync(context);
        await VerifyLiveWorkerPreservedAsync(context);
        await VerifyInspectionFailuresAsync(context);
        await VerifyPendingAdmissionsAsync(context);
        await VerifyLaunchFailureAfterAdmissionAsync(context);
        await VerifyPreLaunchChecksAsync(context);
        await VerifyUnreadableAdmissionAsync(context);
    }

    // Cases 1 and 3: an exited PID is proven stale at startup, and its old token cannot revive the record.
    private static async Task VerifyDeadPidRecoveryAsync(AdmissionContext context)
    {
        int deadPid;
        using (var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") {
            UseShellExecute = false, CreateNoWindow = true
        })!) {
            exited.WaitForExit();
            deadPid = exited.Id;
        }
        var admission = context.CreateCase("dead-pid", context.Record(context.Session, deadPid));
        await using var coordinator = admission.CreateCoordinator(context.WorkerPath);
        Require(!admission.RecordExists && !admission.ManifestExists
            && coordinator.Snapshot.Observation == WorkerObservation.WorkerNotStarted, "已退出 PID 的 Admission 未在启动时清理。");

        var rejected = false;
        try {
            await using var pipe = await OpenConnectionAsync(admission.PipeName, admission.Record, 0, context.Token);
        } catch (Exception exception) when (exception is EndOfStreamException or IOException
            or InvalidOperationException) {
            rejected = true;
        }
        Require(rejected && !admission.RecordExists, "已清理的 Admission 被旧令牌握手复活。");
    }

    // Case 2: a Worker alive at startup is kept; once it exits, Prepare proves it stale and removes the record.
    private static async Task VerifyWorkerExitAfterStartupAsync(AdmissionContext context)
    {
        var alive = true;
        var admission = context.CreateCase("exit-after-startup", context.Record(context.Session, 4242));
        // The extensionless, differently cased name still identifies the same Worker image.
        await using var coordinator = admission.CreateCoordinator(context.MissingWorkerPath, () => alive
            ? [new WorkerProcessEntry(4242, context.Session, "narutoautoworker")]
            : []);
        Require(admission.RecordExists && coordinator.Snapshot.Observation == WorkerObservation.IpcDisconnected,
            "存活 Worker 的 Admission 在启动时被清理。");
        alive = false;
        await ExpectAsync<FileNotFoundException>(() => coordinator.PrepareWorkerAsync(
            context.Session, context.Project, context.Token), "准备运行环境前未清理已退出 Worker 的 Admission。");
        Require(!admission.RecordExists && !admission.ManifestExists, "准备前恢复未删除失效 Admission。");
    }

    // Cases 4 and 5: a PID now owned by another image or Session proves the recorded Worker gone.
    private static async Task VerifyReusedPidRecoveryAsync(AdmissionContext context)
    {
        using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") {
            UseShellExecute = false, CreateNoWindow = true
        })!;
        try {
            var reused = context.CreateCase("reused-image", context.Record(context.Session, other.Id));
            await using (reused.CreateCoordinator(context.WorkerPath)) {
                Require(!reused.RecordExists, "PID 被其他映像复用时未判定为失效。");
            }
        } finally {
            other.Kill(entireProcessTree: true);
        }

        var moved = context.CreateCase("other-session", context.Record(context.Session, 4243));
        await using (moved.CreateCoordinator(context.MissingWorkerPath,
            () => [new WorkerProcessEntry(4243, context.AbsentSession, "NarutoAutoWorker.exe")])) {
            Require(!moved.RecordExists, "PID 位于其他 Session 时未判定为失效。");
        }
    }

    // Case 6: a verified live Worker keeps its record byte for byte and can still prove its identity.
    private static async Task VerifyLiveWorkerPreservedAsync(AdmissionContext context)
    {
        var admission = context.CreateCase("live-worker", context.Record(context.Session, Environment.ProcessId));
        await using var coordinator = admission.CreateCoordinator(context.WorkerPath);
        Require(admission.RecordUnchanged && admission.ManifestExists
            && coordinator.Snapshot.Observation == WorkerObservation.IpcDisconnected, "存活 Worker 的 Admission 被修改。");
        var refusal = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
            context.Session, context.Project, context.Token), "存活 Worker 未阻止准备运行环境。");
        Require(refusal.Message.Contains("仍在运行", StringComparison.Ordinal)
            && refusal.Message.Contains(EndChildSessionText, StringComparison.Ordinal)
            && admission.RecordUnchanged && admission.ManifestExists, "存活 Worker 的拒绝信息或记录不符合预期。");
        await using var pipe = await OpenConnectionAsync(admission.PipeName, admission.Record, 0, context.Token);
        await WaitForFreshAsync(coordinator, context.Token);
    }

    // Case 7: failed inspection or unreadable fields keep the record and refuse Prepare.
    private static async Task VerifyInspectionFailuresAsync(AdmissionContext context)
    {
        (string Name, Func<IReadOnlyList<WorkerProcessEntry>> Capture)[] processCases = [
            ("snapshot-throws", () => throw new Win32Exception(5, "self-test access denied")),
            ("session-unreadable", () => [new WorkerProcessEntry(4244, null, "NarutoAutoWorker")]),
            ("image-unreadable", () => [new WorkerProcessEntry(4244, context.Session, null)])
        ];
        foreach (var (name, capture) in processCases) {
            var admission = context.CreateCase(name, context.Record(context.Session, 4244));
            await using var coordinator = admission.CreateCoordinator(context.MissingWorkerPath, capture);
            await RequireIndeterminateAsync(context, admission, coordinator, name);
        }

        var pending = context.CreateCase("sessions-throw", context.Record(context.AbsentSession, null));
        await using var sessionFailure = pending.CreateCoordinator(context.MissingWorkerPath, () => [],
            () => throw new Win32Exception(5, "self-test session enumeration failure"));
        await RequireIndeterminateAsync(context, pending, sessionFailure, "sessions-throw");
    }

    // Cases 8, 9 and 10: a PID-less record is stale only when its Session is proven gone, never because of age.
    private static async Task VerifyPendingAdmissionsAsync(AdmissionContext context)
    {
        foreach (var age in new[] { TimeSpan.Zero, TimeSpan.FromDays(1) }) {
            var name = $"pending-live-session-{age.TotalHours:0}h";
            var admission = context.CreateCase(name, context.Record(context.Session, null, DateTime.UtcNow - age));
            await using var coordinator = admission.CreateCoordinator(context.WorkerPath);
            await RequireIndeterminateAsync(context, admission, coordinator, name);
            await using var pipe = await OpenConnectionAsync(admission.PipeName, admission.Record, 0, context.Token);
            await WaitForFreshAsync(coordinator, context.Token);
            Require(coordinator.TrackedWorkerPid == Environment.ProcessId
                && admission.ReadRecord().WorkerPid == Environment.ProcessId, $"{name}：保留的 Admission 未能被 Worker 接纳。");
        }

        var future = context.CreateCase("pending-future",
            context.Record(context.AbsentSession, null, DateTime.UtcNow.AddMinutes(10)));
        await using (var coordinator = future.CreateCoordinator(context.MissingWorkerPath)) {
            await RequireIndeterminateAsync(context, future, coordinator, "pending-future");
        }

        var gone = context.CreateCase("pending-session-gone",
            context.Record(context.AbsentSession, null, DateTime.UtcNow.AddMinutes(-1)));
        await using (var coordinator = gone.CreateCoordinator(context.MissingWorkerPath)) {
            Require(!gone.RecordExists && !gone.ManifestExists
                && coordinator.Snapshot.Observation == WorkerObservation.WorkerNotStarted,
                "Session 已证明不存在时未清理 PID 为空的 Admission。");
        }
    }

    // Case 11: once written, a PID-less Admission survives a launch exception unless its Session is proven gone.
    private static async Task VerifyLaunchFailureAfterAdmissionAsync(AdmissionContext context)
    {
        var launches = 0;
        var options = new MaaFrameworkOptions(SaveOnError: false, SaveDraw: true, DebugMode: true);
        var kept = context.CreateCase("launch-failure-session-exists", null);
        await using (var coordinator = kept.CreateCoordinator(kept.FakeWorkerPath,
            () => [new WorkerProcessEntry(4, context.Session, "csrss")], () => [context.Session],
            (_, instanceId, _, manifestPath, _) => {
                var manifest = System.Text.Json.JsonSerializer.Deserialize<LaunchManifest>(
                    File.ReadAllBytes(manifestPath), ProtocolJson.Options)!;
                Require(manifest.WorkerInstanceId == instanceId && manifest.FrameworkOptions == options,
                    "GUI 设置未写入实际启动 Worker 所用的 Launch Manifest。");
                launches++;
                throw new InvalidOperationException("scripted launch failure");
            })) {
            var failure = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
                context.Session, context.Project, context.Token, options), "注入的启动失败没有传播。");
            Require(failure.Message == "scripted launch failure" && kept.ReadRecord().WorkerPid is null
                && coordinator.Snapshot.Observation == WorkerObservation.WorkerRecoveryConflict,
                "启动失败后 PID 为空的 Admission 未被保留。");
            var refusal = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
                context.Session, context.Project, context.Token), "保留的 Admission 未阻止再次准备。");
            Require(launches == 1 && refusal.Message.Contains(EndChildSessionText, StringComparison.Ordinal),
                "保留的 Admission 被再次启动越过。");
        }

        var gone = context.CreateCase("launch-failure-session-gone", null);
        await using (var coordinator = gone.CreateCoordinator(gone.FakeWorkerPath, () => [], () => [],
            (_, _, _, _, _) => throw new InvalidOperationException("scripted launch failure"))) {
            await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
                context.AbsentSession, context.Project, context.Token), "注入的启动失败没有传播。");
            Require(!gone.RecordExists && !gone.LaunchDirectoryHasFiles, "Session 已证明不存在时未回滚启动失败的 Admission。");
        }
    }

    // Cases 12 and 13: a missing executable or an occupied Session fails before any Admission is written.
    private static async Task VerifyPreLaunchChecksAsync(AdmissionContext context)
    {
        var missing = context.CreateCase("missing-executable", null);
        await using (var coordinator = missing.CreateCoordinator(context.MissingWorkerPath)) {
            var started = TrackStarting(coordinator);
            await ExpectAsync<FileNotFoundException>(() => coordinator.PrepareWorkerAsync(
                context.Session, context.Project, context.Token), "缺失 Worker 可执行文件时未失败。");
            Require(!started() && !missing.RecordExists && !missing.LaunchDirectoryHasFiles,
                "缺失 Worker 可执行文件时仍写入了 Admission。");
        }

        var occupied = context.CreateCase("occupied-session", null);
        await using (var coordinator = occupied.CreateCoordinator(context.WorkerPath)) {
            var started = TrackStarting(coordinator);
            var refusal = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
                context.Session, context.Project, context.Token), "目标 Session 已有 Worker 时仍准备启动。");
            Require(refusal.Message.Contains(Environment.ProcessId.ToString(), StringComparison.Ordinal)
                && !started() && !occupied.RecordExists, "会话非空时未在写入 Admission 前拒绝。");
        }

        var unreadable = context.CreateCase("occupied-unreadable", null);
        await using (var coordinator = unreadable.CreateCoordinator(unreadable.FakeWorkerPath, () => [
            new WorkerProcessEntry(4246, context.Session, null), new WorkerProcessEntry(4247, null, "NarutoAutoWorker")
        ])) {
            var started = TrackStarting(coordinator);
            var refusal = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
                context.Session, context.Project, context.Token), "无法读取的进程未阻止启动。");
            Require(refusal.Message.Contains("4246", StringComparison.Ordinal)
                && refusal.Message.Contains("4247", StringComparison.Ordinal)
                && !started() && !unreadable.RecordExists, "无法读取的进程未在写入 Admission 前拒绝。");
        }
    }

    // Case 14: an unreadable worker.json blocks Prepare until the Child Session is confirmed ended.
    private static async Task VerifyUnreadableAdmissionAsync(AdmissionContext context)
    {
        var admission = context.CreateCase("unreadable-record", null);
        Directory.CreateDirectory(admission.StateDirectory);
        File.WriteAllText(admission.RecordPath, "not json");
        await using var coordinator = admission.CreateCoordinator(context.MissingWorkerPath);
        var started = TrackStarting(coordinator);
        Require(coordinator.Snapshot.Observation == WorkerObservation.WorkerRecoveryConflict,
            "不可读的 Admission 未显示为恢复冲突。");
        var refusal = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
            context.Session, context.Project, context.Token), "不可读的 Admission 未阻止准备。");
        Require(refusal.Message.Contains(EndChildSessionText, StringComparison.Ordinal) && !started()
            && File.ReadAllText(admission.RecordPath) == "not json", "不可读的 Admission 被覆盖或未给出操作提示。");
        coordinator.ChildSessionEnded();
        await ExpectAsync<FileNotFoundException>(() => coordinator.PrepareWorkerAsync(
            context.Session, context.Project, context.Token), "结束桌面分身后仍阻止准备。");
    }

    private static async Task RequireIndeterminateAsync(
        AdmissionContext context, AdmissionCase admission, WorkerCoordinator coordinator, string name)
    {
        Require(admission.RecordUnchanged && admission.ManifestExists
            && coordinator.Snapshot.Observation == WorkerObservation.WorkerRecoveryConflict,
            $"{name}：无法确认时启动恢复修改了 Admission。");
        var started = TrackStarting(coordinator);
        var refusal = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
            context.Session, context.Project, context.Token), $"{name}：无法确认时未拒绝准备。");
        Require(refusal.Message.Contains(EndChildSessionText, StringComparison.Ordinal) && !started()
            && admission.RecordUnchanged && admission.ManifestExists, $"{name}：无法确认时拒绝准备后记录被修改。");
    }

    private static Func<bool> TrackStarting(WorkerCoordinator coordinator)
    {
        var started = false;
        coordinator.StateChanged += (_, snapshot) =>
            started |= snapshot.Observation == WorkerObservation.WorkerStarting;
        return () => started;
    }

    private static async Task WaitForFreshAsync(WorkerCoordinator coordinator, CancellationToken cancellationToken)
    {
        while (coordinator.Snapshot is not { Observation: WorkerObservation.Connected, SnapshotFresh: true }) {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action, string failure) where T : Exception
    {
        try {
            await action();
        } catch (T exception) {
            return exception;
        }
        throw new InvalidOperationException(failure);
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) {
            throw new InvalidOperationException(failure);
        }
    }

    // A Session ID that neither WTS nor any process uses, so its absence is provable during the test.
    private static uint FindAbsentSession()
    {
        var sessions = WorkerAdmissionInspection.EnumerateSessionIds();
        var processes = WorkerAdmissionInspection.CaptureProcesses();
        for (var candidate = 0x7FFF_0000u; ; candidate++) {
            if (!sessions.Contains(candidate) && processes.All(process => process.SessionId != candidate)) {
                return candidate;
            }
        }
    }

    private sealed record AdmissionContext(
        AppLogger Logger, string Directory, string WorkerPath, uint Session, ProjectPlanModule Project,
        uint AbsentSession, CancellationToken Token)
    {
        internal string MissingWorkerPath => Path.Combine(Directory, "missing", "NarutoAutoWorker.exe");

        internal WorkerAdmissionRecord Record(uint session, int? pid, DateTime? createdAtUtc = null) => new(
            Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(), session, pid,
            "self-test-runtime", createdAtUtc ?? DateTime.UtcNow);

        internal AdmissionCase CreateCase(string name, WorkerAdmissionRecord? record)
        {
            var admission = new AdmissionCase(this, Path.Combine(Directory, name), record);
            if (record is not null) {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(admission.ManifestPath)!);
                File.WriteAllText(admission.ManifestPath, "{}");
                File.WriteAllBytes(admission.RecordPath,
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options));
                admission.OriginalBytes = File.ReadAllBytes(admission.RecordPath);
            }
            return admission;
        }
    }

    private sealed class AdmissionCase(AdmissionContext context, string stateDirectory, WorkerAdmissionRecord? record)
    {
        internal string StateDirectory { get; } = stateDirectory;
        internal WorkerAdmissionRecord Record => record ?? throw new InvalidOperationException("该用例没有预置记录。");
        internal string PipeName { get; } = $"NarutoAutoGUI.Worker.SelfTest.{Guid.NewGuid():N}";
        internal string RecordPath => Path.Combine(StateDirectory, "worker.json");
        internal string LaunchDirectory => Path.Combine(StateDirectory, "launch");
        internal string ManifestPath => Path.Combine(LaunchDirectory, $"{Record.WorkerInstanceId:N}.json");
        internal byte[]? OriginalBytes { get; set; }
        internal bool RecordExists => File.Exists(RecordPath);
        internal bool ManifestExists => File.Exists(ManifestPath);
        internal bool RecordUnchanged => RecordExists && File.ReadAllBytes(RecordPath).SequenceEqual(OriginalBytes!);
        internal bool LaunchDirectoryHasFiles =>
            System.IO.Directory.Exists(LaunchDirectory) && System.IO.Directory.EnumerateFiles(LaunchDirectory).Any();

        // Launching is skipped by the tests, so the file only needs to exist with a Worker-like name.
        internal string FakeWorkerPath
        {
            get
            {
                var path = Path.Combine(StateDirectory, "fake-worker", "NarutoAutoWorker.exe");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) {
                    File.WriteAllBytes(path, []);
                }
                return path;
            }
        }

        internal WorkerAdmissionRecord ReadRecord() =>
            System.Text.Json.JsonSerializer.Deserialize<WorkerAdmissionRecord>(
                File.ReadAllBytes(RecordPath), ProtocolJson.Options)!;

        internal WorkerCoordinator CreateCoordinator(
            string workerPath, Func<IReadOnlyList<WorkerProcessEntry>>? captureProcesses = null,
            Func<IReadOnlyCollection<uint>>? enumerateSessions = null,
            Func<uint, Guid, string, string, CancellationToken, Task<VerifiedChildSessionProcessLaunch>>? launch = null)
            => new(context.Logger, StateDirectory, workerPath, PipeName, usePipeAcl: false,
                captureProcesses, enumerateSessions, launch);
    }
}
