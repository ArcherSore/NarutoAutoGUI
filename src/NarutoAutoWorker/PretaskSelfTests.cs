using System.Diagnostics;
using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class PretaskSelfTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(8);

    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"NarutoAutoGUI.Pretask.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try {
            await VerifyArgumentsAsync(directory);
            await VerifyOutputBudgetAsync(directory);
            await VerifyChildrenAsync(directory, "spawn-success", RuntimeExecutionOutcome.Succeeded);
            await VerifyChildrenAsync(directory, "spawn-fail", RuntimeExecutionOutcome.Failed);
            await VerifyChildrenAsync(directory, "spawn-wait", RuntimeExecutionOutcome.Failed,
                TimeSpan.FromSeconds(3));
            await VerifyStopAsync(directory);
            await VerifyHandoffRaceAsync(directory, stopBeforeCommit: true);
            await VerifyHandoffRaceAsync(directory, stopBeforeCommit: false);
            await VerifyFactoryFailureAsync(directory);
            await VerifyUnconfirmedAsync(directory);
        } finally {
            foreach (var path in Directory.EnumerateFiles(directory, "*.pid", SearchOption.AllDirectories)) {
                StopTestProcess(path);
            }
            Directory.Delete(directory, true);
        }
    }

    private static async Task VerifyArgumentsAsync(string directory)
    {
        var folder = NewFolder(directory, "arguments");
        string[] values = ["", "space value", "trailing\\", "quote\"value", "\\\"", "中文路径\\",
            "{\"账号\":{\"value\":\"with space\\\\\"}}"];
        var logs = new List<string>();
        var execution = Execution(folder, "arguments", (_, _, message) => logs.Add(message), values: values);
        var result = await ExecuteAndCommitAsync(execution).WaitAsync(WaitLimit);
        var captured = JsonSerializer.Deserialize<ArgumentCapture>(
            File.ReadAllText(Path.Combine(folder, "args.json")))!;
        Require(result.Outcome == RuntimeExecutionOutcome.Succeeded && captured.Arguments.SequenceEqual(values)
            && captured.Directory == folder && captured.Session == Process.GetCurrentProcess().SessionId,
            "Pretask 必须原样传递空值、引号、末尾反斜杠和中文 JSON，并继承项目目录与 Worker Session。 ");
    }

    private static async Task VerifyOutputBudgetAsync(string directory)
    {
        var folder = NewFolder(directory, "output");
        var count = 0;
        var truncations = 0;
        var execution = Execution(folder, "output", (level, _, message) => {
            Interlocked.Add(ref count, message.Length);
            if (level == "WARN" && message.Contains("截断", StringComparison.Ordinal)) {
                Interlocked.Increment(ref truncations);
            }
        });
        var result = await ExecuteAndCommitAsync(execution).WaitAsync(WaitLimit);
        Require(result.Outcome == RuntimeExecutionOutcome.Succeeded && count < 40000 && truncations == 2,
            "大量 stdout/stderr 不应堵塞程序，且每路输出只保留有界内容。 ");
    }

    private static async Task VerifyChildrenAsync(string directory, string mode, RuntimeExecutionOutcome expected,
        TimeSpan? timeout = null)
    {
        var folder = NewFolder(directory, mode);
        var execution = Execution(folder, mode, (_, _, _) => { }, timeout);
        var timer = Stopwatch.StartNew();
        var result = await ExecuteAndCommitAsync(execution).WaitAsync(WaitLimit);
        var identity = Path.Combine(folder, "child.pid");
        Require(File.Exists(identity), "测试程序必须启动自己的子进程。 ");
        Require(result.Outcome == expected, $"{mode} 结果不符：{result.Outcome}，{result.Error?.Message}");
        Require(IsTestProcessAlive(identity) == (expected == RuntimeExecutionOutcome.Succeeded),
            "成功必须保留新游戏进程；非零或超时必须确认本次进程树已清空。 ");
        if (expected == RuntimeExecutionOutcome.Succeeded) {
            Require(timer.Elapsed < TimeSpan.FromSeconds(5), "根成功不得等待长驻子进程持有的 stdout EOF。 ");
        } else if (mode == "spawn-wait") {
            Require(result.Error?.Code == "PretaskTimeout", "程序超时且清理确认应为普通失败。 ");
        }
        StopTestProcess(identity);
    }

    private static async Task VerifyStopAsync(string directory)
    {
        var folder = NewFolder(directory, "stop");
        var execution = Execution(folder, "spawn-wait", (_, _, _) => { });
        var running = execution.ExecuteAsync(CancellationToken.None);
        await WaitForFileAsync(Path.Combine(folder, "child.pid"));
        execution.RequestStop();
        await Task.Delay(100);
        Require(!running.IsCompleted && IsTestProcessAlive(Path.Combine(folder, "child.pid")),
            "RequestStop 只能记停止标志，ACK 前不得终止进程。 ");
        await execution.StopAsync(CancellationToken.None).WaitAsync(WaitLimit);
        var result = await running;
        Require(result.Outcome == RuntimeExecutionOutcome.Cancelled
            && !IsTestProcessAlive(Path.Combine(folder, "child.pid")), "停止必须在进程树清空后确认。 ");

        var before = Execution(NewFolder(directory, "before"), "spawn-wait", (_, _, _) => { });
        before.RequestStop();
        var pending = before.ExecuteAsync(CancellationToken.None);
        Require(!pending.IsCompleted, "尚未 ACK 的停止请求不应提前提交终态。 ");
        await before.StopAsync(CancellationToken.None).WaitAsync(WaitLimit);
        Require((await pending).Outcome == RuntimeExecutionOutcome.Cancelled,
            "程序创建前的停止也应确认 Cancelled，不能启动游戏。 ");
    }

    private static async Task<RuntimeExecutionResult> ExecuteAndCommitAsync(PretaskExecution execution)
    {
        var result = await execution.ExecuteAsync(CancellationToken.None);
        return result.Outcome == RuntimeExecutionOutcome.Succeeded ? execution.CommitSuccess(result) : result;
    }

    private static async Task VerifyHandoffRaceAsync(string directory, bool stopBeforeCommit)
    {
        var folder = NewFolder(directory, stopBeforeCommit ? "stop-before-handoff" : "stop-after-handoff");
        var empty = ProtocolJson.ToElement(new { });
        var item = new RunPlanItem(Guid.NewGuid(), "launch", "启动测试程序", "", empty, empty) {
            Kind = PlanItemKind.Pretask, Program = HelperCommand("spawn-handoff", folder)
        };
        var manifest = new LaunchManifest(ProtocolConstants.LaunchContextVersion, Guid.NewGuid(),
            RunHarness.ProfileDigest, folder, RunHarness.Project, null!, [], null!);
        var supervisor = new RunSupervisor(manifest, new WorkerLogBuffer(),
            (_, entry, onRunning) => new PretaskExecution(manifest, entry, (_, _, _) => { }, onRunning),
            (_, _, _, _) => { }, (_, _) => { }, CancellationToken.None);
        var ok = new DependencyCheck(true, "test", null);
        supervisor.CompleteInitialization(new DependencyStatus(DateTime.UtcNow, "test", "test",
            ok, ok, ok, ok, ok), null);
        var plan = new RunPlan(ProtocolConstants.PlanVersion, DateTime.UtcNow, RunHarness.Project,
            RunHarness.ProfileDigest, empty, [item]);
        var request = new RunStartRequest(Guid.NewGuid(), CanonicalDigest.ComputePlanDigestV2(plan), plan);
        supervisor.Accept(request);
        await WaitForFileAsync(Path.Combine(folder, "child.pid"));
        if (stopBeforeCommit) {
            var gate = typeof(RunSupervisor).GetField("_stateGate", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(supervisor)!;
            // Hold the same commit gate while the root exits, so stop deterministically wins before handoff.
            lock (gate) {
                File.WriteAllText(Path.Combine(folder, "root.exit"), "exit");
                var timer = Stopwatch.StartNew();
                while (IsTestProcessAlive(Path.Combine(folder, "parent.pid"))) {
                    Require(timer.Elapsed < WaitLimit, "测试根程序未退出。 ");
                    Thread.Sleep(25);
                }
                supervisor.Stop(new RunStopRequest(request.RunId), out var pending);
                pending!.Begin();
            }
        } else {
            File.WriteAllText(Path.Combine(folder, "root.exit"), "exit");
        }
        var wait = Stopwatch.StartNew();
        while (supervisor.GetSnapshot().LastRun is null) {
            Require(wait.Elapsed < WaitLimit, "Pretask 交接竞争未终结。 ");
            await Task.Delay(25);
        }
        var terminal = supervisor.GetSnapshot().LastRun!;
        Require(terminal.State == (stopBeforeCommit ? RunState.Cancelled : RunState.Succeeded)
            && IsTestProcessAlive(Path.Combine(folder, "child.pid")) != stopBeforeCommit,
            "停止胜出必须清理子进程；成功提交胜出才可保留子进程。 ");
        if (!stopBeforeCommit) {
            Require(supervisor.Stop(new RunStopRequest(request.RunId), out _) is { Disposition: "already_terminal" },
                "成功交接与终态提交后，迟到的停止不得把成功改成取消。 ");
        }
        StopTestProcess(Path.Combine(folder, "child.pid"));
    }

    private static async Task VerifyFactoryFailureAsync(string directory)
    {
        var folder = NewFolder(directory, "factory-failure");
        var empty = ProtocolJson.ToElement(new { });
        var first = new RunPlanItem(Guid.NewGuid(), "launch", "启动测试程序", "", empty, empty) {
            Kind = PlanItemKind.Pretask, Program = HelperCommand("spawn-success", folder)
        };
        var next = new RunPlanItem(Guid.NewGuid(), "task", "后续任务", "Entry", empty, empty);
        var manifest = new LaunchManifest(ProtocolConstants.LaunchContextVersion, Guid.NewGuid(),
            RunHarness.ProfileDigest, folder, RunHarness.Project, null!, [], null!);
        PretaskExecution? execution = null;
        var supervisor = new RunSupervisor(manifest, new WorkerLogBuffer(), (_, entry, onRunning) => {
            if (entry.Kind == PlanItemKind.MaaTask) {
                throw new InvalidOperationException("scripted next factory failure");
            }
            execution = new PretaskExecution(manifest, entry, (_, _, _) => { }, onRunning);
            return execution;
        }, (_, _, _, _) => { }, (_, _) => { }, CancellationToken.None);
        var ok = new DependencyCheck(true, "test", null);
        supervisor.CompleteInitialization(new DependencyStatus(DateTime.UtcNow, "test", "test",
            ok, ok, ok, ok, ok), null);
        var plan = new RunPlan(ProtocolConstants.PlanVersion, DateTime.UtcNow, RunHarness.Project,
            RunHarness.ProfileDigest, empty, [first, next]);
        supervisor.Accept(new RunStartRequest(Guid.NewGuid(), CanonicalDigest.ComputePlanDigestV2(plan), plan));
        var timer = Stopwatch.StartNew();
        while (supervisor.GetSnapshot().WorkerState != WorkerState.Faulted) {
            Require(timer.Elapsed < WaitLimit, "后续执行器创建失败后 Worker 未进入 Faulted。 ");
            await Task.Delay(25);
        }
        var snapshot = supervisor.GetSnapshot();
        var process = (PretaskProcess?)typeof(PretaskExecution).GetField("_process",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(execution);
        Require(snapshot is { LastRun: null, ActiveRun: not null, WorkerReason.Code: "RunLoopFault" }
            && process is not null && IsTestProcessAlive(Path.Combine(folder, "child.pid")),
            "后续 factory 失败时不能提前交接子进程；ActiveRun 与原进程所有权必须保留。 ");
        Require(await process!.TerminateAndConfirmAsync(WaitLimit), "测试恢复必须清空本次程序树。 ");
        await process.CloseOutputAsync();
        process.Dispose();
    }

    private static async Task VerifyUnconfirmedAsync(string directory)
    {
        var folder = NewFolder(directory, "unconfirmed");
        var execution = Execution(folder, "spawn-wait", (_, _, _) => { },
            TimeSpan.FromSeconds(3), TimeSpan.Zero);
        var result = await ExecuteAndCommitAsync(execution).WaitAsync(WaitLimit);
        Require(result.Outcome == RuntimeExecutionOutcome.StopTimedOut
            && result.Error?.Code == "PretaskTerminationUnconfirmed", "未确认清理不得报为普通 Failed 或 Cancelled。 ");
        // The deliberately expired confirmation budget leaves ownership retained, even if termination completed.
        var process = (PretaskProcess)typeof(PretaskExecution).GetField("_process",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(execution)!;
        Require(await process.TerminateAndConfirmAsync(WaitLimit), "测试恢复必须确认本次树退出。 ");
        await process.CloseOutputAsync();
        process.Dispose();
    }

    private static PretaskExecution Execution(string directory, string mode, Action<string, string, string> log,
        TimeSpan? timeout = null, TimeSpan? cleanup = null, IReadOnlyList<string>? values = null)
    {
        var command = HelperCommand(mode, directory, values);
        var empty = ProtocolJson.ToElement(new { });
        var item = new RunPlanItem(Guid.NewGuid(), "launch", "启动测试程序", "", empty, empty) {
            Kind = PlanItemKind.Pretask, Program = command
        };
        var manifest = new LaunchManifest(ProtocolConstants.LaunchContextVersion, Guid.NewGuid(), "test", directory,
            RunHarness.Project, null!, [], null!);
        return new PretaskExecution(manifest, item, log, () => { }, timeout, cleanup);
    }

    private static PretaskCommand HelperCommand(string mode, string directory, IReadOnlyList<string>? values = null)
    {
        var executable = Environment.ProcessPath!;
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
            arguments.Add(typeof(Program).Assembly.Location);
        }
        arguments.AddRange(["--pretask-test-child", mode, directory]);
        arguments.AddRange(values ?? []);
        return new PretaskCommand(executable, arguments);
    }

    // A real child process exercises native argument parsing, job inheritance and inherited pipe handles.
    internal static int RunChild(string[] args)
    {
        var mode = args[1];
        var directory = args[2];
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var process = Process.GetCurrentProcess();
        var identity = Path.Combine(directory, mode == "linger" ? "child.pid" : "parent.pid");
        File.WriteAllText(identity + ".tmp",
            JsonSerializer.Serialize(new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks)));
        File.Move(identity + ".tmp", identity);
        if (mode == "arguments") {
            File.WriteAllText(Path.Combine(directory, "args.json"), JsonSerializer.Serialize(
                new ArgumentCapture(args[3..], Environment.CurrentDirectory, process.SessionId)));
            return 0;
        }
        if (mode == "output") {
            var block = new string('x', 4096);
            for (var index = 0; index < 512; index++) {
                Console.Out.Write(block);
                Console.Error.Write(block);
            }
            return 0;
        }
        if (mode.StartsWith("spawn-", StringComparison.Ordinal)) {
            var command = HelperCommand("linger", directory);
            var start = new ProcessStartInfo(command.Executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in command.Arguments) {
                start.ArgumentList.Add(argument);
            }
            using var child = Process.Start(start) ?? throw new InvalidOperationException("无法创建测试子进程。 ");
            WaitForFileAsync(Path.Combine(directory, "child.pid")).GetAwaiter().GetResult();
            if (mode == "spawn-handoff") {
                WaitForFileAsync(Path.Combine(directory, "root.exit")).GetAwaiter().GetResult();
                return 0;
            }
            if (mode != "spawn-wait") {
                return mode == "spawn-success" ? 0 : 9;
            }
        }
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    }

    private static string NewFolder(string directory, string name)
    {
        var folder = Path.Combine(directory, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path)) {
            Require(timer.Elapsed < WaitLimit, "测试子进程就绪超时。 ");
            await Task.Delay(25);
        }
    }

    private static bool IsTestProcessAlive(string path)
    {
        var identity = JsonSerializer.Deserialize<ProcessIdentity>(File.ReadAllText(path))!;
        try {
            using var process = Process.GetProcessById(identity.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.Started;
        } catch (ArgumentException) {
            return false;
        }
    }

    private static void StopTestProcess(string path)
    {
        if (!IsTestProcessAlive(path)) {
            return;
        }
        var identity = JsonSerializer.Deserialize<ProcessIdentity>(File.ReadAllText(path))!;
        using var process = Process.GetProcessById(identity.Pid);
        if (process.StartTime.ToUniversalTime().Ticks == identity.Started) {
            process.Kill();
            Require(process.WaitForExit(5000), "无法退出本次测试子进程。 ");
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ProcessIdentity(int Pid, long Started);
    private sealed record ArgumentCapture(string[] Arguments, string Directory, int Session);
}
