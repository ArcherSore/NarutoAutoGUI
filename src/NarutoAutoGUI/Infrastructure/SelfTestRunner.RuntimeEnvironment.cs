using System.ComponentModel;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

// Ends the Runtime Environment through RuntimeEnvironment alone and observes simulated WTS, the pipe and the
// coordinator. Each end reason keeps the steps its caller took before the lifecycle had a single owner.
internal static partial class SelfTestRunner
{
    private static readonly TimeSpan EnvironmentWaitLimit = TimeSpan.FromMilliseconds(500);

    private static void VerifyRuntimeEnvironment(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "runtime-environment");
        VerifyExitEnd(logger, directory);
        VerifyExitEndUnconfirmed(logger, directory);
        VerifyEndWithoutStop(logger, directory);
        VerifyInstallEnd(logger, directory);
        VerifyInstallEndUnconfirmed(logger, directory);
        VerifyEnvironmentClosing(logger, directory);
        Console.WriteLine("RUNTIME ENVIRONMENT PASS: exit, exit unconfirmed, user end, install, install unconfirmed, "
            + "closing.");
    }

    // E1, E3: exit asks only when a Child Session exists and only after any in-flight operation, then discards the
    // Admission before another operation can run. It does not wait for the Worker.
    private static void VerifyExitEnd(AppLogger logger, string directory)
    {
        var none = new SimulatedWts();
        using (var session = none.CreateManager(logger)) {
            WithFakeWorker(logger, Path.Combine(directory, "exit-none"), (coordinator, _, _) => {
                var asked = false;
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator)
                    .EndAsync(EnvironmentEndReason.Exit, () => asked = true));
                Require(ended == EnvironmentEndResult.Ended && !asked && none.LogoffAttempts.Count == 0,
                    "没有桌面分身时退出不应询问或注销。");
            });
        }

        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var existing = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "exit"), (coordinator, _, _) => {
            var waits = 0;
            var environment = CreateEnvironment(logger, existing, coordinator, (_, _) => {
                waits++;
                return Task.CompletedTask;
            });
            var declined = OnPool(async () => {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = environment.RunExclusiveAsync(() => release.Task);
                var ending = environment.EndAsync(EnvironmentEndReason.Exit, () => {
                    asked.TrySetResult();
                    return false;
                });
                Require(!asked.Task.Wait(200), "退出确认应等待进行中的操作完成后再询问。");
                release.SetResult();
                await held;
                return await ending;
            });
            Require(declined == EnvironmentEndResult.Declined && wts.LogoffAttempts.Count == 0
                && coordinator.TrackedWorkerPid is not null, "拒绝退出时不应注销或丢弃 Admission。");

            var (ended, discardedForNext) = OnPool(async () => {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = environment.RunExclusiveAsync(() => release.Task);
                var ending = environment.EndAsync(EnvironmentEndReason.Exit, () => true);
                var discarded = false;
                var next = environment.RunExclusiveAsync(() => {
                    discarded = AdmissionDiscarded(coordinator);
                    return Task.CompletedTask;
                });
                release.SetResult();
                await Task.WhenAll(held, next);
                return (await ending, discarded);
            });
            Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([7u]) && discardedForNext
                && waits == 0, "确认退出后应注销分身，在下一操作开始前丢弃 Admission，且不等待 Worker 退出。");
        });
    }

    // E2: an exit that cannot log off or check the Child Session is cancelled. A failed logoff keeps the Admission;
    // the final check runs after the Admission has already been discarded.
    private static void VerifyExitEndUnconfirmed(AppLogger logger, string directory)
    {
        foreach (var (name, wts, message, discarded) in new (string, SimulatedWts, string, bool)[] {
            ("exit-logoff-failure", new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], LogoffError = 5 },
                "注销桌面分身失败，已取消退出，避免遗留未知状态。请查看日志。", false),
            ("exit-session-survives",
                new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], SessionSurvivesLogoff = true },
                "最终检查仍检测到桌面分身，已取消退出，避免遗留未知状态。请查看日志后重试。", true)
        }) {
            using var session = wts.CreateManager(logger);
            WithFakeWorker(logger, Path.Combine(directory, name), (coordinator, _, _) => {
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator)
                    .EndAsync(EnvironmentEndReason.Exit, () => true));
                Require(ended is { Outcome: EnvironmentEndOutcome.Unconfirmed } && ended.Error?.Message == message
                    && AdmissionDiscarded(coordinator) == discarded, $"{name}：退出应取消并说明原因。");
            });
        }

        using var unreadable = new ChildSessionManager(logger,
            () => throw new Win32Exception(5, "self-test WTS failure"), () => [0u, 1u], () => null);
        WithFakeWorker(logger, Path.Combine(directory, "exit-query-failure"), (coordinator, _, _) => {
            var asked = false;
            var ended = OnPool(() => CreateEnvironment(logger, unreadable, coordinator)
                .EndAsync(EnvironmentEndReason.Exit, () => asked = true));
            Require(ended is { Outcome: EnvironmentEndOutcome.Unconfirmed } && !asked
                && ended.Error?.Message == "无法确认桌面分身状态，已取消退出。请查看日志后重试。"
                && coordinator.TrackedWorkerPid is not null, "无法查询桌面分身时退出应取消且不询问。");
        });
    }

    // E1, E4: exit and the user's end log off a running Run without asking the Worker to stop it. The user's end also
    // skips the final WTS check; both are current behaviour that #23 PR 2 changes.
    private static void VerifyEndWithoutStop(AppLogger logger, string directory)
    {
        foreach (var reason in new[] { EnvironmentEndReason.Exit, EnvironmentEndReason.UserRequested }) {
            var requests = new List<string>();
            using var listening = new CancellationTokenSource();
            var wts = new SimulatedWts {
                ReportedId = 7, Sessions = [0, 1, 7],
                SessionSurvivesLogoff = reason == EnvironmentEndReason.UserRequested
            };
            using var session = wts.CreateManager(logger);
            WithRunningFakeWorker(logger, Path.Combine(directory, $"no-stop-{reason}"),
                (worker, token) => RecordRequestsAsync(worker.Pipe, requests, token, listening.Token), coordinator => {
                    try {
                        AwaitRunningWorker(coordinator);
                        var waits = 0;
                        var ended = OnPool(() => CreateEnvironment(logger, session, coordinator, (_, _) => {
                            waits++;
                            return Task.CompletedTask;
                        }).EndAsync(reason, () => true));
                        Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([7u])
                            && AdmissionDiscarded(coordinator) && waits == 0,
                            $"{reason}：应注销并丢弃 Admission，不等待 Worker 退出。");
                    } finally {
                        listening.Cancel();
                    }
                });
            Require(requests.Count == 0, $"{reason}：结束分身前不应请求 Worker 停止 Run。");
        }
    }

    // E5: install stops the Active Run and waits for it to end, logs off, checks WTS, waits for the Worker captured
    // before logoff, and only then discards the Admission. A stop that is not acknowledged does not block it.
    private static void VerifyInstallEnd(AppLogger logger, string directory)
    {
        WithRunningFakeWorker(logger, Path.Combine(directory, "install"), async (worker, token) => {
            var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(worker.Pipe, ProtocolOperations.RunStop, token);
            await worker.Pipe.WriteAsync(WireEnvelope.Response(ProtocolOperations.RunStop, stop.RequestId!.Value,
                new RunStopResponse("stop_requested")), token);
            var cancelled = worker.Coordinator.Snapshot.WorkerSnapshot! with {
                StateRevision = 2, RunState = RunState.Idle, ActiveRun = null,
                LastRun = worker.ActiveRun with { State = RunState.Cancelled }
            };
            await worker.Pipe.WriteAsync(StateEvent(cancelled), token);
        }, coordinator => {
            AwaitRunningWorker(coordinator);
            var logoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool? runEndedFirst = null;
            var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
            wts.DuringLogoff = _ => {
                runEndedFirst = coordinator.Snapshot.WorkerSnapshot?.ActiveRun is null;
                logoff.TrySetResult();
            };
            using var session = wts.CreateManager(logger);
            int? waitedPid = null;
            // The wait only completes after logoff, so the end succeeds only if it waits after logging off.
            var ended = OnPool(() => CreateEnvironment(logger, session, coordinator, (pid, _) => {
                waitedPid = pid;
                return logoff.Task;
            }, TimeSpan.FromSeconds(10)).EndAsync(EnvironmentEndReason.Install));
            Require(ended == EnvironmentEndResult.Ended && runEndedFirst == true && waitedPid == Environment.ProcessId
                && AdmissionDiscarded(coordinator), "安装应先停止 Run 并等待其结束，再注销、等待 Worker 退出并丢弃 Admission。");
        });

        WithRunningFakeWorker(logger, Path.Combine(directory, "install-stop-unanswered"), async (worker, token) =>
            await WorkerCoordinatorSelfTest.ReadRequestAsync(worker.Pipe, ProtocolOperations.RunStop, token),
            coordinator => {
                AwaitRunningWorker(coordinator);
                var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
                using var session = wts.CreateManager(logger);
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator)
                    .EndAsync(EnvironmentEndReason.Install));
                Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([7u])
                    && AdmissionDiscarded(coordinator), "Run 停止未确认时安装仍应注销分身。");
            });
    }

    // E5: install is cancelled, keeping the Admission, when WTS still reports the Child Session, the Worker does not
    // exit in time or the logoff fails; the logoff failure is passed on unchanged.
    private static void VerifyInstallEndUnconfirmed(AppLogger logger, string directory)
    {
        foreach (var (name, wts, exits, message) in new (string, SimulatedWts, bool, string?)[] {
            ("install-session-survives",
                new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], SessionSurvivesLogoff = true },
                true, "无法确认桌面分身已结束，已取消安装。请查看日志后重试。"),
            ("install-worker-stays", new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] },
                false, "无法确认 Worker 已退出，已取消安装。请查看日志后重试。"),
            ("install-logoff-failure", new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], LogoffError = 5 },
                true, null)
        }) {
            using var session = wts.CreateManager(logger);
            WithFakeWorker(logger, Path.Combine(directory, name), (coordinator, _, _) => {
                var waitEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator, async (_, token) => {
                    try {
                        await Task.Delay(exits ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, token);
                    } finally {
                        waitEnded.TrySetResult(token.IsCancellationRequested);
                    }
                }).EndAsync(EnvironmentEndReason.Install));
                Require(ended is { Outcome: EnvironmentEndOutcome.Unconfirmed }
                    && (message is null ? ended.Error is Win32Exception : ended.Error is IOException
                        && ended.Error.Message == message)
                    && coordinator.TrackedWorkerPid is not null
                    && (exits || waitEnded.Task.Wait(5000) && waitEnded.Task.Result),
                    $"{name}：安装应取消、保留 Admission 并停止等待 Worker。");
            });
        }
    }

    // E6: while the application is closing, operations and the user's end are refused; exit still ends.
    private static void VerifyEnvironmentClosing(AppLogger logger, string directory)
    {
        using var session = new SimulatedWts().CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "closing"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, session, coordinator);
            environment.Closing = true;
            var ran = false;
            Require(Refused(() => environment.RunExclusiveAsync(() => {
                ran = true;
                return Task.CompletedTask;
            })) && !ran, "退出中不应开始新的运行环境操作。");
            Require(Refused(() => environment.EndAsync(EnvironmentEndReason.UserRequested)),
                "退出中不应开始用户发起的结束分身。");
            Require(OnPool(() => environment.EndAsync(EnvironmentEndReason.Exit)) == EnvironmentEndResult.Ended,
                "退出本身不受退出中标志阻止。");
        });

        static bool Refused(Func<Task> action)
        {
            try {
                Task.Run(action).GetAwaiter().GetResult();
                return false;
            } catch (OperationCanceledException) {
                return true;
            }
        }
    }

    private static RuntimeEnvironment CreateEnvironment(AppLogger logger, ChildSessionManager session,
        WorkerCoordinator coordinator, Func<int, CancellationToken, Task>? waitForWorkerExit = null,
        TimeSpan? waitLimit = null) => new(logger, session, coordinator, new ChildSessionProgramService(logger),
        waitForWorkerExit ?? ((_, _) => Task.CompletedTask), waitLimit ?? EnvironmentWaitLimit);

    // The owner a window under test uses; operations run at once unless a test holds them.
    private static RuntimeEnvironment WindowEnvironment(AppLogger logger, ChildSessionManager session,
        WorkerCoordinator coordinator, Func<Func<Task>, Task>? runOperation = null) =>
        new(logger, session, coordinator, new ChildSessionProgramService(logger),
            runExclusive: runOperation ?? (operation => operation()));

    // Ending before the fake Worker is admitted would discard the Admission it connects with.
    private static void AwaitRunningWorker(WorkerCoordinator coordinator) => Require(SpinWait.SpinUntil(
        () => coordinator.Snapshot is { Observation: WorkerObservation.Connected, SnapshotFresh: true,
            WorkerSnapshot.ActiveRun: not null }, 10000), "假 Worker 未报告运行中的 Run。");

    private static bool AdmissionDiscarded(WorkerCoordinator coordinator) =>
        coordinator.Snapshot.Observation == WorkerObservation.ChildSessionEnded && coordinator.TrackedWorkerPid is null;

    // Runs on the thread pool, away from the dispatcher the self-test blocks.
    private static T OnPool<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

    private static async Task RecordRequestsAsync(ProtocolConnection pipe, List<string> requests,
        CancellationToken cancellationToken, CancellationToken stopListening)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopListening);
        try {
            while (await pipe.ReadAsync(linked.Token) is { } envelope) {
                lock (requests) {
                    requests.Add(envelope.Operation);
                }
            }
        } catch (Exception exception) when (exception is OperationCanceledException or IOException) {
        }
    }
}
