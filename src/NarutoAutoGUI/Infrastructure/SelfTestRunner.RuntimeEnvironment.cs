using System.ComponentModel;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Worker;

namespace NarutoAutoGUI.Infrastructure;

// Ends the Runtime Environment through RuntimeEnvironment alone and observes simulated WTS, the pipe and the
// coordinator. Every caller gets the same end: stop the Active Run, log off, confirm that the Child Session and the
// Worker are gone, and only then discard the Admission.
internal static partial class SelfTestRunner
{
    private static readonly TimeSpan EnvironmentWaitLimit = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OnPoolLimit = TimeSpan.FromSeconds(30);

    private static void VerifyRuntimeEnvironment(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "runtime-environment");
        VerifyEndStopsRunFirst(logger, directory);
        VerifyEndAfterUnconfirmedStop(logger, directory);
        VerifyEndWithFaultedWorker(logger, directory);
        VerifyEndWaitsForWorker(logger, directory);
        VerifyEndUnconfirmed(logger, directory);
        VerifyEndConfirmation(logger, directory);
        VerifyEndHoldsGate(logger, directory);
        VerifyEnvironmentClosing(logger, directory);
        Console.WriteLine("RUNTIME ENVIRONMENT PASS: stop first, unconfirmed stop, faulted Worker, Worker exit, "
            + "unconfirmed end, confirmation, gate, closing.");
    }

    // U1, U9: the Active Run is stopped and has ended before logoff, and the Admission is discarded only after the
    // Worker has exited. The end is in progress from asking the user to the last step.
    private static void VerifyEndStopsRunFirst(AppLogger logger, string directory)
    {
        RuntimeEnvironment? environment = null;
        bool? endingAtStop = null;
        WithRunningFakeWorker(logger, Path.Combine(directory, "stop-first"), async (worker, token) => {
            var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(worker.Pipe, ProtocolOperations.RunStop, token);
            endingAtStop = environment?.Ending;
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
            (bool RunEnded, bool Ending, bool Discarded)? atLogoff = null;
            var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
            wts.DuringLogoff = _ => {
                atLogoff = (coordinator.Snapshot.WorkerSnapshot?.ActiveRun is null, environment!.Ending,
                    AdmissionDiscarded(coordinator));
                logoff.TrySetResult();
            };
            using var session = wts.CreateManager(logger);
            int? waitedPid = null;
            // The wait only completes after logoff, so the end succeeds only if it waits after logging off.
            environment = CreateEnvironment(logger, session, coordinator, (pid, _) => {
                waitedPid = pid;
                return logoff.Task;
            }, TimeSpan.FromSeconds(10));
            var changes = 0;
            environment.StateChanged += (_, _) => Interlocked.Increment(ref changes);
            bool? endingAtConfirm = null;
            var ended = OnPool(() => environment.EndAsync(() => {
                endingAtConfirm = environment.Ending;
                return true;
            }));
            Require(ended == EnvironmentEndResult.Ended && atLogoff == (true, true, false)
                && waitedPid == Environment.ProcessId && AdmissionDiscarded(coordinator),
                "结束应先停止 Run 并等待其结束，再注销、等待 Worker 退出，最后丢弃 Admission。");
            Require(endingAtConfirm == true && endingAtStop == true && !environment.Ending && changes == 2
                && !environment.EndUnconfirmed, "结束期间应一直处于正在结束，结束后清除并各通知一次。");
        });
    }

    // U2: a stop that the Worker rejects, never answers or cannot answer because it disconnected does not block the
    // end; logging off ends the Run.
    private static void VerifyEndAfterUnconfirmedStop(AppLogger logger, string directory)
    {
        foreach (var outcome in new[] { "rejected", "unanswered", "disconnected" }) {
            WithRunningFakeWorker(logger, Path.Combine(directory, $"stop-{outcome}"), async (worker, token) => {
                var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                    worker.Pipe, ProtocolOperations.RunStop, token);
                if (outcome == "rejected") {
                    await worker.Pipe.WriteAsync(WireEnvelope.Failure(ProtocolOperations.RunStop,
                        stop.RequestId!.Value, "operation_not_allowed", "scripted stop rejection"), token);
                } else if (outcome == "disconnected") {
                    await worker.Pipe.DisposeAsync();
                }
            }, coordinator => {
                AwaitRunningWorker(coordinator);
                var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
                using var session = wts.CreateManager(logger);
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator).EndAsync(() => true));
                Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([7u])
                    && AdmissionDiscarded(coordinator), $"{outcome}：Run 停止未确认时仍应注销分身并完成结束。");
            });
        }
    }

    // U3: a Faulted Worker whose stop is unconfirmed is not asked again; logging off ends its Run.
    private static void VerifyEndWithFaultedWorker(AppLogger logger, string directory)
    {
        var requests = new List<string>();
        using var listening = new CancellationTokenSource();
        WithRunningFakeWorker(logger, Path.Combine(directory, "faulted"), async (worker, token) => {
            WorkerSnapshot? snapshot;
            while ((snapshot = worker.Coordinator.Snapshot.WorkerSnapshot) is null) {
                await Task.Delay(20, token);
            }
            var faulted = snapshot with {
                StateRevision = 2, WorkerState = WorkerState.Faulted,
                WorkerReason = new StructuredReason("StopTimeout", "scripted stop timeout"),
                RunState = RunState.Stopping, ActiveRun = worker.ActiveRun with { State = RunState.Stopping }
            };
            await worker.Pipe.WriteAsync(StateEvent(faulted), token);
            await RecordRequestsAsync(worker.Pipe, requests, token, listening.Token);
        }, coordinator => {
            try {
                AwaitWorker(coordinator, snapshot => snapshot.WorkerSnapshot?.WorkerState == WorkerState.Faulted,
                    "假 Worker 未报告 Faulted。");
                var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
                using var session = wts.CreateManager(logger);
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator).EndAsync(() => true));
                Require(ended == EnvironmentEndResult.Ended && wts.LogoffAttempts.SequenceEqual([7u])
                    && AdmissionDiscarded(coordinator), "Worker Faulted 时应直接注销分身并完成结束。");
            } finally {
                listening.Cancel();
            }
        });
        Require(requests.Count == 0, "Worker Faulted 时不应再请求停止 Run。");
    }

    // U4: an end is confirmed only once the Worker has exited within the limit. Otherwise the Admission is kept and
    // the end is reported unconfirmed; ending again continues from what remains and clears that report.
    private static void VerifyEndWaitsForWorker(AppLogger logger, string directory)
    {
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var session = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "worker-exit"), (coordinator, _, _) => {
            var exited = false;
            var waitEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var environment = CreateEnvironment(logger, session, coordinator, async (_, token) => {
                try {
                    await Task.Delay(exited ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, token);
                } finally {
                    waitEnded.TrySetResult(token.IsCancellationRequested);
                }
            });
            var first = OnPool(() => environment.EndAsync(() => true));
            Require(first is { Outcome: EnvironmentEndOutcome.Unconfirmed, Error.Message: "无法确认 Worker 已退出。" }
                && coordinator.TrackedWorkerPid is not null && environment.EndUnconfirmed
                && waitEnded.Task.Wait(5000) && waitEnded.Task.Result,
                "Worker 未在上限内退出时，结束应未确认、保留 Admission 并停止等待。");

            exited = true;
            var asked = false;
            var second = OnPool(() => environment.EndAsync(() => asked = true));
            Require(second == EnvironmentEndResult.Ended && asked && AdmissionDiscarded(coordinator)
                && !environment.EndUnconfirmed && wts.LogoffAttempts.SequenceEqual([7u]),
                "再次结束应确认 Worker 退出后丢弃 Admission，并清除未确认。");
        });
    }

    // U5, U10: a failed logoff, a Child Session that survives logoff and a WTS query that fails all leave the end
    // unconfirmed with the Admission kept. A Child Session that cannot be queried is not asked about.
    private static void VerifyEndUnconfirmed(AppLogger logger, string directory)
    {
        foreach (var (name, createSession, message, asks) in new (string, Func<ChildSessionManager>, string, bool)[] {
            ("logoff-failure", () => new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], LogoffError = 5 }
                .CreateManager(logger), "注销桌面分身失败。", true),
            ("session-survives", () => new SimulatedWts {
                ReportedId = 7, Sessions = [0, 1, 7], SessionSurvivesLogoff = true
            }.CreateManager(logger), "无法确认桌面分身已结束。", true),
            ("query-failure", () => new ChildSessionManager(logger,
                () => throw new Win32Exception(5, "self-test WTS failure"), () => [0u, 1u], () => null),
                "无法确认桌面分身状态。", false)
        }) {
            using var session = createSession();
            WithFakeWorker(logger, Path.Combine(directory, name), (coordinator, _, _) => {
                var asked = false;
                var environment = CreateEnvironment(logger, session, coordinator);
                var ended = OnPool(() => environment.EndAsync(() => asked = true));
                Require(ended is { Outcome: EnvironmentEndOutcome.Unconfirmed } && ended.Error?.Message == message
                    && asked == asks && coordinator.TrackedWorkerPid is not null && environment.EndUnconfirmed
                    && !environment.Ending, $"{name}：结束应未确认、说明原因并保留 Admission。");
            });
        }
    }

    // U7, U8: the user is asked only when a Runtime Environment may remain, and only after any in-flight operation;
    // declining changes nothing. An Admission without a Child Session still needs ending.
    private static void VerifyEndConfirmation(AppLogger logger, string directory)
    {
        var none = new SimulatedWts();
        using (var session = none.CreateManager(logger)) {
            var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "none", "state"), "unused.exe",
                $"NarutoAutoGUI.RuntimeEnvironment.SelfTest.{Guid.NewGuid():N}", usePipeAcl: false);
            try {
                var asked = false;
                var ended = OnPool(() => CreateEnvironment(logger, session, coordinator).EndAsync(() => asked = true));
                Require(ended == EnvironmentEndResult.Ended && !asked && none.LogoffAttempts.Count == 0,
                    "没有运行环境时结束不应询问或注销。");
            } finally {
                Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
            }
        }

        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using (var existing = wts.CreateManager(logger)) {
            WithFakeWorker(logger, Path.Combine(directory, "declined"), (coordinator, _, _) => {
                var environment = CreateEnvironment(logger, existing, coordinator);
                var declined = OnPool(async () => {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var held = environment.RunExclusiveAsync(() => release.Task);
                    var ending = environment.EndAsync(() => {
                        asked.TrySetResult();
                        return false;
                    });
                    Require(!asked.Task.Wait(200), "确认应等待进行中的操作完成后再询问。");
                    release.SetResult();
                    await held;
                    return await ending;
                });
                Require(declined == EnvironmentEndResult.Declined && wts.LogoffAttempts.Count == 0
                    && coordinator.TrackedWorkerPid is not null && !environment.EndUnconfirmed && !environment.Ending,
                    "拒绝结束时不应注销、丢弃 Admission 或报告未确认。");
            });
        }

        using var admissionOnly = new SimulatedWts().CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "admission-only"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, admissionOnly, coordinator);
            var asked = 0;
            var ended = OnPool(() => environment.EndAsync(() => ++asked > 0));
            Require(ended == EnvironmentEndResult.Ended && asked == 1 && AdmissionDiscarded(coordinator),
                "只剩 Admission 时仍应询问并结束。");
            Require(OnPool(() => environment.EndAsync(() => ++asked > 0)) == EnvironmentEndResult.Ended && asked == 1,
                "结束确认后再次结束不应询问。");
        });
    }

    // U6: the end waits for the in-flight operation and discards the Admission inside the gate, so a queued operation
    // sees it discarded; the caller handles the result before that operation runs.
    private static void VerifyEndHoldsGate(AppLogger logger, string directory)
    {
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var session = wts.CreateManager(logger);
        WithFakeWorker(logger, Path.Combine(directory, "gate"), (coordinator, _, _) => {
            var environment = CreateEnvironment(logger, session, coordinator);
            var (ended, seen, discardedForNext) = OnPool(async () => {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var held = environment.RunExclusiveAsync(() => release.Task);
                var queued = false;
                (bool HeldDone, bool QueuedRan)? seen = null;
                var ending = environment.EndAsync(() => true, _ => {
                    seen = (release.Task.IsCompleted, queued);
                    return Task.CompletedTask;
                });
                var discarded = false;
                var next = environment.RunExclusiveAsync(() => {
                    queued = true;
                    discarded = AdmissionDiscarded(coordinator);
                    return Task.CompletedTask;
                });
                release.SetResult();
                await Task.WhenAll(held, next);
                return (await ending, seen, discarded);
            });
            Require(ended == EnvironmentEndResult.Ended && seen == (true, false) && discardedForNext,
                "结束应等待进行中的操作，在后续操作前处理结果，且后续操作看到 Admission 已丢弃。");
        });
    }

    // While the application is closing, operations are refused; the end that closes it still runs.
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
            Require(OnPool(() => environment.EndAsync(() => true)) == EnvironmentEndResult.Ended,
                "退出中的结束不受退出中标志阻止。");
        });

        static bool Refused(Func<Task> action)
        {
            try {
                Task.Run(action).WaitAsync(OnPoolLimit).GetAwaiter().GetResult();
                return false;
            } catch (OperationCanceledException) {
                return true;
            }
        }
    }

    private static RuntimeEnvironment CreateEnvironment(AppLogger logger, ChildSessionManager session,
        WorkerCoordinator coordinator, Func<int, CancellationToken, Task>? waitForWorkerExit = null,
        TimeSpan? waitLimit = null) => new(logger, session, coordinator,
        waitForWorkerExit ?? ((_, _) => Task.CompletedTask), waitLimit ?? EnvironmentWaitLimit);

    // The owner a window under test uses; operations run at once unless a test holds them, and the fake Worker,
    // which is this process, counts as exited once the Child Session is logged off.
    private static RuntimeEnvironment WindowEnvironment(AppLogger logger, ChildSessionManager session,
        WorkerCoordinator coordinator, Func<Func<Task>, Task>? runOperation = null) =>
        new(logger, session, coordinator, (_, _) => Task.CompletedTask,
            EnvironmentWaitLimit, runOperation ?? (operation => operation()));

    // Ending before the fake Worker is admitted would discard the Admission it connects with.
    private static void AwaitRunningWorker(WorkerCoordinator coordinator) => AwaitWorker(coordinator,
        snapshot => snapshot is { Observation: WorkerObservation.Connected, SnapshotFresh: true,
            WorkerSnapshot.ActiveRun: not null }, "假 Worker 未报告运行中的 Run。");

    private static void AwaitWorker(WorkerCoordinator coordinator, Func<WorkerCoordinatorSnapshot, bool> condition,
        string failure) => Require(SpinWait.SpinUntil(() => condition(coordinator.Snapshot), 10000), failure);

    private static bool AdmissionDiscarded(WorkerCoordinator coordinator) =>
        coordinator.Snapshot.Observation == WorkerObservation.ChildSessionEnded && coordinator.TrackedWorkerPid is null;

    // Runs on the thread pool, away from the dispatcher the self-test blocks; a hang fails instead of stalling.
    private static T OnPool<T>(Func<Task<T>> action) =>
        Task.Run(action).WaitAsync(OnPoolLimit).GetAwaiter().GetResult();

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
