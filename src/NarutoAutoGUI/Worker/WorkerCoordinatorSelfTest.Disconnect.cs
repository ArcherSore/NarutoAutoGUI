using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static partial class WorkerCoordinatorSelfTest
{
    // A killed Worker closes its pipe end without an error, so a plain EOF must end Connected. The observation then
    // follows the recorded PID, the Admission stays until Prepare proves it stale, and a late cleanup of the old
    // connection never overwrites an ended Child Session.
    private static async Task VerifyWorkerPipeEofAsync(
        AppLogger logger, string testDirectory, string workerPath, uint session, ProjectPlanModule project)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var stateDirectory = Path.Combine(testDirectory, "worker-eof");
        var recordPath = Path.Combine(stateDirectory, "worker.json");
        var pipeName = $"NarutoAutoGUI.Worker.SelfTest.{Guid.NewGuid():N}";
        var record = new WorkerAdmissionRecord(
            Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(), session,
            Environment.ProcessId, project.RuntimeProfileDigest, DateTime.UtcNow);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllBytes(recordPath,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options));

        var alive = true;
        Action? duringInspection = null;
        IReadOnlyList<WorkerProcessEntry> CaptureProcesses()
        {
            Interlocked.Exchange(ref duringInspection, null)?.Invoke();
            return alive ? [new WorkerProcessEntry(Environment.ProcessId, session, Path.GetFileName(workerPath))] : [];
        }
        Task<ProtocolConnection>? replacement = null;
        var launches = 0;
        await using var coordinator = new WorkerCoordinator(logger, stateDirectory, workerPath, pipeName,
            usePipeAcl: false, CaptureProcesses, () => [session], (sessionId, instanceId, launchToken, _, _) =>
            {
                launches++;
                replacement = OpenConnectionAsync(pipeName, new WorkerAdmissionRecord(instanceId, launchToken,
                    sessionId, null, project.RuntimeProfileDigest, DateTime.UtcNow), 0, token);
                return Task.FromResult(new VerifiedChildSessionProcessLaunch(
                    (uint)Environment.ProcessId, sessionId, null, null));
            });
        await coordinator.WaitForServerReadyAsync(token);

        // A live recorded PID: EOF leaves Connected for IpcDisconnected and Prepare waits for that Worker.
        var pipe = await OpenConnectionAsync(pipeName, record, 0, token);
        await WaitForFreshAsync(coordinator, token);
        await pipe.DisposeAsync();
        await WaitForObservationAsync(coordinator, WorkerObservation.IpcDisconnected, token);
        Require(!coordinator.Snapshot.SnapshotFresh && File.Exists(recordPath), "EOF 后仍显示 fresh 或删除了 Admission。");
        var refusal = await ExpectAsync<InvalidOperationException>(() => coordinator.PrepareWorkerAsync(
            session, project, token), "Worker 仍存活时 EOF 后准备运行环境未被拒绝。");
        Require(refusal.Message.Contains("仍在运行", StringComparison.Ordinal),
            $"EOF 后旧连接未解绑，准备运行环境得到：{refusal.Message}");

        // A killed Worker's pipe ends while Windows still lists its process; once the process is gone the observation
        // becomes WorkerExited without another Prepare, and the Admission still stays.
        alive = false;
        await WaitForObservationAsync(coordinator, WorkerObservation.WorkerExited, token);
        Require(File.Exists(recordPath), "Worker 进程退出后仅凭观察就删除了 Admission。");
        alive = true;

        // The server keeps accepting; a dead recorded PID then shows WorkerExited and Prepare replaces it.
        pipe = await OpenConnectionAsync(pipeName, record, 0, token);
        await WaitForFreshAsync(coordinator, token);
        alive = false;
        await pipe.DisposeAsync();
        await WaitForObservationAsync(coordinator, WorkerObservation.WorkerExited, token);
        Require(File.Exists(recordPath), "仅因 Pipe 关闭就删除了 Admission。");
        var replaced = await coordinator.PrepareWorkerAsync(session, project, token);
        Require(launches == 1 && replaced.WorkerInstanceId != record.WorkerInstanceId
            && coordinator.TrackedWorkerPid == Environment.ProcessId, "Worker 退出后准备运行环境未启动替代 Worker。");
        alive = true;

        // Child Session ends before the old connection is cleaned up.
        coordinator.ChildSessionEnded();
        await (await replacement!).DisposeAsync();
        await Task.Delay(200, token);
        Require(coordinator.Snapshot.Observation == WorkerObservation.ChildSessionEnded,
            "旧连接的清理覆盖了 Child Session 已结束。");

        // Child Session ends while the old connection's disconnect is still inspecting processes.
        alive = false;
        await coordinator.PrepareWorkerAsync(session, project, token);
        alive = true;
        var ended = false;
        duringInspection = () =>
        {
            coordinator.ChildSessionEnded();
            ended = true;
        };
        await (await replacement!).DisposeAsync();
        await WaitForObservationAsync(coordinator, WorkerObservation.ChildSessionEnded, token);
        await Task.Delay(200, token);
        Require(ended && coordinator.Snapshot.Observation == WorkerObservation.ChildSessionEnded,
            "迟到的断线观察覆盖了 Child Session 已结束。");
    }

    private static async Task WaitForObservationAsync(
        WorkerCoordinator coordinator, WorkerObservation observation, CancellationToken cancellationToken)
    {
        while (coordinator.Snapshot.Observation != observation) {
            await Task.Delay(10, cancellationToken);
        }
    }
}
