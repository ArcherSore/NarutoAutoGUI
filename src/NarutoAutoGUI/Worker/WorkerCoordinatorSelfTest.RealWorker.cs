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
                (worker, workerErrors) = StartRealWorker(workerPath,
                    ChildSessionWorkerLauncher.FormatArguments(instanceId, launchToken, manifestPath),
                    errorEncoding, token);
                return Task.FromResult(new VerifiedChildSessionProcessLaunch((uint)worker.Id, sessionId, null, null));
            });
        coordinator.LogReceived += (_, entry) => logs.Enqueue(entry);
        // The manifest holds the defaults, so SaveDraw can only turn on through the update after connecting.
        coordinator.FrameworkOptions = () => new MaaFrameworkOptions(SaveDraw: true);
        try {
            await coordinator.ApplyFrameworkOptionsAsync(token);
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
            while (!logs.Any(entry => entry.Source == "runtime.options" && entry.Message.Contains("SaveDraw=True"))) {
                await Task.Delay(20, token);
            }

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

    // Real MaaFramework keeps recognising in its own process while the real cleaner deletes what it has written so far.
    private static async Task VerifyRealWorkerCleanWhileRunningAsync(AppLogger logger, string testDirectory)
    {
        var workerPath = FindRealWorker()
            ?? throw new SelfTestSkip("未提供 --worker-exe；verify.ps1 -Suite Worker 或 All 构建 Worker 后才会运行。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var root = Path.Combine(testDirectory, "clean while running");
        var vision = Path.Combine(root, "debug", "vision");
        var frameworkLog = Path.Combine(root, "debug", "maafw.log");
        string[] Images() => Directory.Exists(vision) ? Directory.GetFiles(vision, "*.jpg") : [];
        var (process, errors) = StartRealWorker(workerPath,
            $"--framework-diagnostics-self-test \"{root}\" keep-running", Encoding.UTF8, token);
        using var worker = process;
        async Task WaitFor(Func<bool> condition)
        {
            while (!condition()) {
                if (worker.HasExited) {
                    throw new InvalidOperationException($"任务进程提前退出，exit={worker.ExitCode}：{await errors}");
                }
                await Task.Delay(20, token);
            }
        }

        try {
            await WaitFor(() => Images().Length > 0 && File.Exists(frameworkLog));
            var cleaner = new DebugFileCleaner(root, Path.Combine(root, "logs"), logger);
            var written = new FileInfo(frameworkLog).Length;
            var result = cleaner.Clean();
            Require(result.Deleted > 0, "运行中清理应删除已写入的图片。");
            Require(File.Exists(frameworkLog), "正在写入的 MaaFramework 日志不应被删除。");

            // Anything left was written during the clean; only a name not yet seen proves writing went on after it.
            var seen = Images().ToHashSet(StringComparer.OrdinalIgnoreCase);
            await WaitFor(() => Images().Any(image => !seen.Contains(image))
                && new FileInfo(frameworkLog).Length > written);

            await File.WriteAllTextAsync(Path.Combine(root, "stop"), "", token);
            await worker.WaitForExitAsync(token);
            Require(worker.ExitCode == 0, $"清理后任务应继续正常运行，exit={worker.ExitCode}：{await errors}");

            cleaner.Clean();
            Require(!File.Exists(frameworkLog), "进程退出后，MaaFramework 日志应能被清理。");
        } finally {
            if (!worker.HasExited) {
                worker.Kill();
            }
        }
    }

    // Console logging is drained and dropped: what a test asserts arrives over the pipe or on disk.
    private static (Process Worker, Task<string> Errors) StartRealWorker(string path, string arguments,
        Encoding errorEncoding, CancellationToken token)
    {
        var worker = Process.Start(new ProcessStartInfo(path, arguments) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = errorEncoding
        }) ?? throw new InvalidOperationException("无法启动 Worker。");
        worker.BeginOutputReadLine();
        return (worker, worker.StandardError.ReadToEndAsync(token));
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
