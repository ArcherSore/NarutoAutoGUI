using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal static partial class WorkerCoordinatorSelfTest
{
    // The real Worker executable over the real pipe name and ACL. Task Scheduler and the Child Session stay out of
    // reach, so the process starts directly, with the same arguments production hands to the scheduler.
    private static async Task VerifyRealWorkerAsync(AppLogger logger, string testDirectory, ProjectPlanModule project)
    {
        var workerPath = FindRealWorker()
            ?? throw new SelfTestSkip("未提供 --worker-exe；verify.ps1 -Suite Worker 或 All 构建 Worker 后才会运行。");
        var pipeName = PipeIdentity.ForCurrentUser();
        if (Directory.EnumerateFiles(@"\\.\pipe\").Any(path =>
            string.Equals(Path.GetFileName(path), pipeName, StringComparison.OrdinalIgnoreCase))) {
            throw new SelfTestSkip("NarutoAutoGUI 正在运行，占用了 Worker 的 Pipe。");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var session = CurrentSession;
        Process? worker = null;
        Task<string>? workerErrors = null;
        var logs = new ConcurrentQueue<WorkerLogEntry>();
        // A hidden console writes in the OEM code page; read otherwise, a failing Worker's reason comes back garbled.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var errorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        // The space makes the manifest path depend on its quoting, as it does under a profile like "C:\Users\A B".
        await using var coordinator = new WorkerCoordinator(logger, Path.Combine(testDirectory, "state dir"),
            workerPath, pipeName, usePipeAcl: true,
            launchWorker: (sessionId, instanceId, launchToken, manifestPath, _) =>
            {
                worker = Process.Start(new ProcessStartInfo(workerPath,
                    ChildSessionWorkerLauncher.FormatArguments(instanceId, launchToken, manifestPath)) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = errorEncoding
                }) ?? throw new InvalidOperationException("无法启动 Worker。");
                // Its console logging is drained and dropped; the same entries are asserted over the pipe.
                worker.BeginOutputReadLine();
                workerErrors = worker.StandardError.ReadToEndAsync(token);
                return Task.FromResult(new VerifiedChildSessionProcessLaunch((uint)worker.Id, sessionId, null, null));
            });
        coordinator.LogReceived += (_, entry) => logs.Enqueue(entry);
        try {
            var prepare = coordinator.PrepareWorkerAsync(session, project, token);
            while (!prepare.IsCompleted) {
                // Waiting out the admission timeout would only hide why the process ended.
                if (worker is { HasExited: true }) {
                    throw new InvalidOperationException(
                        $"Worker 未完成接纳就退出，exit={worker.ExitCode}：{await workerErrors!}");
                }
                await Task.Delay(20, token);
            }
            var admitted = await prepare;
            Require(admitted.WorkerPid == worker!.Id && admitted.ChildSessionId == session
                && admitted.RuntimeProfileDigest == project.RuntimeProfileDigest, "接纳的不是刚启动的 Worker。");

            // Readiness and startup logs arrive only over the live connection, whichever way this machine's checks end.
            while (coordinator.Snapshot.WorkerSnapshot?.WorkerState is null or WorkerState.Starting || logs.IsEmpty) {
                await Task.Delay(20, token);
            }
            Require(coordinator.Snapshot.WorkerSnapshot!.WorkerState != WorkerState.Faulted, "Worker 初始化进入 Faulted。");

            worker.Kill();
            await worker.WaitForExitAsync(token);
            await WaitForObservationAsync(coordinator, WorkerObservation.WorkerExited, token);
        } finally {
            if (worker is { HasExited: false }) {
                worker.Kill();
            }
            worker?.Dispose();
        }
    }

    // An explicit path that is wrong must fail; only a build without a Worker beside it is a skip.
    private static string? FindRealWorker()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--worker-exe");
        if (index >= 0 && index + 1 < args.Length) {
            return Path.GetFullPath(args[index + 1]);
        }
        var packaged = Path.Combine(AppContext.BaseDirectory, "worker", "NarutoAutoWorker.exe");
        return File.Exists(packaged) ? packaged : null;
    }
}
