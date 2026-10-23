using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
            await VerifyOutputEntryBudgetAsync();
            await VerifyPreservedOutputAsync(directory);
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
        var output = OutputBudget();
        var execution = Execution(folder, "output", output.Log);
        var result = await ExecuteAndCommitAsync(execution).WaitAsync(WaitLimit);
        Require(result.Outcome == RuntimeExecutionOutcome.Succeeded && output.Bounded(),
            "大量 stdout/stderr 不应堵塞程序，且每路输出只保留有界内容。 ");
    }

    // A child writing one character at a time yields one read per write; far below the character budget, the entry
    // budget still bounds the log while the stream is drained to its end.
    private static async Task VerifyOutputEntryBudgetAsync()
    {
        var levels = new List<string>();
        using var process = new PretaskProcess((level, _, _) => levels.Add(level));
        using var output = new TrickleStream(System.Text.Encoding.UTF8.GetBytes(new string('.', 2000)));
        await process.PumpAsync(output, "stdout").WaitAsync(WaitLimit);
        Require(levels.Count(level => level == "INFO") == 256 && levels.Count(level => level == "WARN") == 1
            && output.Position == output.Length, "大量短输出只能记录有界条数，超出后仍须读完全部输出。 ");
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

    private static async Task VerifyPreservedOutputAsync(string directory)
    {
        var folder = NewFolder(directory, "preserved-output");
        var output = OutputBudget();
        var execution = Execution(folder, "spawn-output", output.Log);
        var result = await ExecuteAndCommitAsync(execution).WaitAsync(WaitLimit);
        Require(result.Outcome == RuntimeExecutionOutcome.Succeeded,
            "根程序成功必须及时提交，不能等待仍持有输出管道的后代退出。 ");
        // Release output only after success and disposal, rather than depending on process scheduling delays.
        File.WriteAllText(Path.Combine(folder, "write.output"), "go");
        await WaitForFileAsync(Path.Combine(folder, "output.result"));
        Require(File.ReadAllText(Path.Combine(folder, "output.result")) == "ok",
            "成功保留的子进程必须仍能向继承的 stdout/stderr 写入超过管道容量的数据。 ");
        Require(IsTestProcessAlive(Path.Combine(folder, "child.pid")), "输出后子进程应继续运行。 ");
        Require(output.Bounded(), "根退出后的输出仍必须有界记录，并持续排空超过预算的内容。 ");
        StopTestProcess(Path.Combine(folder, "child.pid"));
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
        var manifest = Manifest(folder);
        var (supervisor, request) = StartRun(manifest,
            (_, entry, onRunning) => new PretaskExecution(manifest, entry, (_, _, _) => { }, onRunning),
            PretaskItem(HelperCommand("spawn-handoff", folder)));
        await WaitForFileAsync(Path.Combine(folder, "child.pid"));
        if (stopBeforeCommit) {
            var gate = typeof(RunSupervisor).GetField("_stateGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(supervisor)!;
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
        await WaitUntilAsync(() => supervisor.GetSnapshot().LastRun is not null, "Pretask 交接竞争未终结。 ");
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
        var manifest = Manifest(folder);
        var empty = ProtocolJson.ToElement(new { });
        var next = new RunPlanItem(Guid.NewGuid(), "task", "后续任务", "Entry", empty, empty);
        PretaskExecution? execution = null;
        var (supervisor, _) = StartRun(manifest, (_, entry, onRunning) => {
            if (entry.Kind == PlanItemKind.MaaTask) {
                throw new InvalidOperationException("scripted next factory failure");
            }
            execution = new PretaskExecution(manifest, entry, (_, _, _) => { }, onRunning);
            return execution;
        }, PretaskItem(HelperCommand("spawn-success", folder)), next);
        await WaitUntilAsync(() => supervisor.GetSnapshot().WorkerState == WorkerState.Faulted,
            "后续执行器创建失败后 Worker 未进入 Faulted。 ");
        var snapshot = supervisor.GetSnapshot();
        var process = OwnedProcess(execution);
        Require(snapshot is { LastRun: null, ActiveRun: not null, WorkerReason.Code: "RunLoopFault" }
            && process is not null && IsTestProcessAlive(Path.Combine(folder, "child.pid")),
            "后续 factory 失败时不能提前交接子进程；ActiveRun 与原进程所有权必须保留。 ");
        Require(await process!.ReleaseAsync(WaitLimit), "测试恢复必须清空本次程序树。 ");
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
        Require(await OwnedProcess(execution)!.ReleaseAsync(WaitLimit), "测试恢复必须确认本次树退出。 ");
    }

    private static PretaskExecution Execution(string directory, string mode, Action<string, string, string> log,
        TimeSpan? timeout = null, TimeSpan? cleanup = null, IReadOnlyList<string>? values = null) =>
        new(Manifest(directory), PretaskItem(HelperCommand(mode, directory, values)), log, () => { }, timeout, cleanup);

    private static (RunSupervisor Supervisor, RunStartRequest Request) StartRun(LaunchManifest manifest,
        Func<Guid, RunPlanItem, Action, IPlanItemExecution> createExecution, params RunPlanItem[] items)
    {
        var supervisor = new RunSupervisor(manifest, new WorkerLogBuffer(), createExecution,
            (_, _, _, _) => { }, (_, _) => { }, CancellationToken.None);
        supervisor.CompleteInitialization(RunHarness.Dependencies, null);
        var request = RunSelfTests.Request(RunSelfTests.Plan(0) with { Items = items });
        supervisor.Accept(request);
        return (supervisor, request);
    }

    private static LaunchManifest Manifest(string directory) => new(ProtocolConstants.LaunchContextVersion,
        Guid.NewGuid(), RunHarness.ProfileDigest, directory, RunHarness.Project, null!, [], null!);

    private static RunPlanItem PretaskItem(PretaskCommand command)
    {
        var empty = ProtocolJson.ToElement(new { });
        return new RunPlanItem(Guid.NewGuid(), "launch", "启动测试程序", "", empty, empty) {
            Kind = PlanItemKind.Pretask, Program = command
        };
    }

    private static PretaskProcess? OwnedProcess(PretaskExecution? execution) =>
        (PretaskProcess?)typeof(PretaskExecution).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(execution);

    // The pumps log from their own threads; bounded output stays under budget and warns once per stream.
    private static (Action<string, string, string> Log, Func<bool> Bounded) OutputBudget()
    {
        var count = 0;
        var truncations = 0;
        void Log(string level, string source, string message)
        {
            Interlocked.Add(ref count, message.Length);
            if (level == "WARN" && message.Contains("截断", StringComparison.Ordinal)) {
                Interlocked.Increment(ref truncations);
            }
        }
        return (Log, () => Volatile.Read(ref count) < 40000 && Volatile.Read(ref truncations) == 2);
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
        var identity = Path.Combine(directory, mode is "linger" or "delayed-output" ? "child.pid" : "parent.pid");
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
        if (mode == "delayed-output") {
            WaitForFileAsync(Path.Combine(directory, "write.output")).GetAwaiter().GetResult();
            var block = new byte[4096];
            Array.Fill(block, (byte)'x');
            var success = true;
            for (var index = 0; index < 512 && success; index++) {
                foreach (var handle in new[] { GetStdHandle(-11), GetStdHandle(-12) }) {
                    success &= WriteFile(handle, block, (uint)block.Length, out var written, IntPtr.Zero)
                        && written == block.Length;
                }
            }
            File.WriteAllText(Path.Combine(directory, "output.result.tmp"), success ? "ok" : "broken pipe");
            File.Move(Path.Combine(directory, "output.result.tmp"), Path.Combine(directory, "output.result"));
        }
        if (mode.StartsWith("spawn-", StringComparison.Ordinal)) {
            var command = HelperCommand(mode == "spawn-output" ? "delayed-output" : "linger", directory);
            var start = new ProcessStartInfo(command.Executable) { UseShellExecute = false, CreateNoWindow = true };
            // Explicit standard handles make stdout/stderr inherit the parent's pipes, as Python Popen does.
            start.RedirectStandardInput = mode == "spawn-output";
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
                return mode is "spawn-success" or "spawn-output" ? 0 : 9;
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

    private static Task WaitForFileAsync(string path) => WaitUntilAsync(() => File.Exists(path), "测试子进程就绪超时。 ");

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) {
            Require(timer.Elapsed < WaitLimit, message);
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int kind);

    // Console.Write can ignore broken-pipe errors; verify the inherited native handles directly.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint size, out uint written, IntPtr overlapped);

    private sealed record ProcessIdentity(int Pid, long Started);
    private sealed record ArgumentCapture(string[] Arguments, string Directory, int Session);

    // Returns one byte per read, like a pipe whose writer flushes every character.
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
