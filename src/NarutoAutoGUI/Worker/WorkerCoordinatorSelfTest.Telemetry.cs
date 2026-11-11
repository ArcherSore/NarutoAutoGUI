using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static partial class WorkerCoordinatorSelfTest
{
    private const string RedactionValue = "redact-me-9137";

    // Protocol v8 as the Worker sees it: the user's telemetry choice travels with the framework switches, and the
    // passwords to redact travel beside the Run Plan, never inside it and never into the GUI log.
    private static async Task VerifyTelemetryProtocolAsync(AppLogger logger, string testDirectory,
        ProjectPlanModule project)
    {
        var stateDirectory = Path.Combine(testDirectory, "worker-telemetry");
        var record = new WorkerAdmissionRecord(Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()),
            CurrentSession, Environment.ProcessId, project.RuntimeProfileDigest, DateTime.UtcNow);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllBytes(Path.Combine(stateDirectory, "worker.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options));
        var pipeName = $"NarutoAutoGUI.Worker.SelfTest.{Guid.NewGuid():N}";
        var written = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Capture(object? sender, LogEntry entry) => written.Enqueue(entry.Message);
        logger.EntryWritten += Capture;
        try {
            await using var coordinator = new WorkerCoordinator(
                logger, stateDirectory, WorkerPath, pipeName, usePipeAcl: false);
            var telemetryEnabled = true;
            coordinator.FrameworkOptions = () => new MaaFrameworkOptions(SaveDraw: true);
            coordinator.TelemetryEnabled = () => telemetryEnabled;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await coordinator.WaitForServerReadyAsync(timeout.Token);
            await using var worker = await OpenConnectionAsync(pipeName, record, 0, timeout.Token);

            async Task<FrameworkSetOptionsRequest> ReadOptionsAsync(bool expected)
            {
                while (true) {
                    var request = await ReadRequestAsync(worker, ProtocolOperations.FrameworkSetOptions, timeout.Token);
                    await worker.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
                        new FrameworkSetOptionsResponse()), timeout.Token);
                    var options = ProtocolJson.Deserialize<FrameworkSetOptionsRequest>(request.Data);
                    if (options.TelemetryEnabled == expected) {
                        return options;
                    }
                }
            }
            var synchronised = await ReadOptionsAsync(expected: true);
            Require(synchronised.Framework == new MaaFrameworkOptions(SaveDraw: true),
                "连接后应同步框架开关与遥测开关。");
            telemetryEnabled = false;
            var applying = coordinator.ApplyFrameworkOptionsAsync(timeout.Token);
            await ReadOptionsAsync(expected: false);
            await applying;

            while (!coordinator.Snapshot.SnapshotFresh) {
                await Task.Delay(20, timeout.Token);
            }
            var attempt = project.CreateRunStartAttempt() with { RedactionValues = [RedactionValue] };
            var starting = coordinator.StartRunAsync(attempt, timeout.Token);
            var start = await ReadRequestAsync(worker, ProtocolOperations.RunStart, timeout.Token);
            await worker.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
                new RunStartResponse("accepted")), timeout.Token);
            await starting;
            var payload = ProtocolJson.Deserialize<RunStartRequest>(start.Data);
            Require(payload.RedactionValues.SequenceEqual([RedactionValue])
                && !start.Data.GetProperty("plan").GetRawText().Contains(RedactionValue, StringComparison.Ordinal)
                && payload.PlanDigest == attempt.PlanDigest
                && CanonicalDigest.ComputePlanDigestV2(payload.Plan) == attempt.PlanDigest,
                "RedactionValues 应随 run.start 下发，且不进入 Run Plan 及其摘要。");
            Require(!written.Any(message => message.Contains(RedactionValue, StringComparison.Ordinal)),
                "RedactionValues 不得写入 GUI 日志。");
        } finally {
            logger.EntryWritten -= Capture;
        }
    }
}
