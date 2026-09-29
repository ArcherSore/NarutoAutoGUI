using System.ComponentModel;
using NarutoAutoGUI.ChildSession;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyChildSessionPresence(AppLogger logger)
    {
        // WTS reporting no Child Session stays authoritative even when session enumeration is unavailable.
        var wts = new SimulatedWts { EnumerationFails = true };
        using (var manager = wts.CreateManager(logger)) {
            Require(!manager.HasChildSession && manager.DetectExistingSession() is null
                && manager.Snapshot.State == ChildSessionState.NotRunning, "没有 Child Session 时不应依赖会话枚举。");
            Require(Terminate(manager) is null && wts.LogoffAttempts.Count == 0,
                "没有 Child Session 时结束分身不应注销任何 Session。");
        }

        wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using (var manager = wts.CreateManager(logger)) {
            Require(manager.HasChildSession && manager.DetectExistingSession() == 7
                && manager.Snapshot.State == ChildSessionState.Existing, "存活的 Child Session 应被检测并恢复。");
            Require(Terminate(manager) is null && wts.LogoffAttempts.SequenceEqual([7u])
                && manager.Snapshot.State == ChildSessionState.NotRunning && !manager.HasChildSession,
                "存活的 Child Session 应被注销，且最终退出检查通过。");
        }

        // A failed RDP logon ends the session, but WTSGetChildSessionId keeps reporting it.
        wts = new SimulatedWts { ReportedId = 10, Sessions = [0, 1] };
        using (var manager = wts.CreateManager(logger)) {
            Require(!manager.HasChildSession, "已不存在的 Child Session 不应阻止退出。");
            Require(manager.DetectExistingSession() is null && manager.Snapshot.State == ChildSessionState.NotRunning,
                "启动时不应恢复已不存在的 Child Session。");
            Require(Terminate(manager) is null && manager.Snapshot.State == ChildSessionState.NotRunning
                && wts.ReportedId == 10 && !manager.HasChildSession, "结束已不存在的 Child Session 应成功。");
            Require(manager.DetectExistingSession() is null, "重启后仍不应把残留 ID 当作已有 Child Session。");
            wts.ReportedId = 11;
            wts.Sessions.Add(11);
            Require(manager.HasChildSession && manager.DetectExistingSession() == 11,
                "重新创建的 Child Session 不应受旧残留 ID 影响。");
        }

        // Without a successful enumeration nothing is proven absent; the verified behavior stays in force.
        wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], EnumerationFails = true };
        using (var manager = wts.CreateManager(logger)) {
            Require(manager.HasChildSession && manager.DetectExistingSession() == 7,
                "无法枚举会话时应按 Child Session 仍存在处理。");
            Require(Terminate(manager) is null && wts.LogoffAttempts.SequenceEqual([7u]),
                "无法枚举会话时仍应按原流程注销 Child Session。");
        }
        wts = new SimulatedWts { ReportedId = 10, Sessions = [0, 1], EnumerationFails = true };
        using (var manager = wts.CreateManager(logger)) {
            Require(manager.HasChildSession, "无法证明残留 ID 已失效时不能放行退出。");
            Require(Terminate(manager) is Win32Exception { NativeErrorCode: 2 }
                && manager.Snapshot.State == ChildSessionState.Faulted, "无法证明 Session 已不存在时注销失败必须保留。");
        }

        // The session was still listed when checked and finished tearing itself down before logoff.
        wts = new SimulatedWts { ReportedId = 9, Sessions = [0, 1, 9] };
        wts.DuringLogoff = simulated => simulated.Sessions.Remove(9);
        using (var manager = wts.CreateManager(logger)) {
            Require(manager.HasChildSession, "正在结束的 Child Session 在消失前仍应视为存在。");
            Require(Terminate(manager) is null && manager.Snapshot.State == ChildSessionState.NotRunning
                && !manager.HasChildSession, "注销时 Session 已自行结束应视为结束成功。");
        }

        wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7], LogoffError = 5 };
        using (var manager = wts.CreateManager(logger)) {
            Require(Terminate(manager) is Win32Exception { NativeErrorCode: 5 }
                && manager.Snapshot.State == ChildSessionState.Faulted && manager.HasChildSession,
                "Session 仍存在时注销失败必须报告失败。");
        }

        wts = new SimulatedWts { ReportedId = 9, Sessions = [0, 1, 9] };
        wts.DuringLogoff = simulated => {
            simulated.Sessions.Remove(9);
            simulated.ReportedId = 12;
            simulated.Sessions.Add(12);
        };
        using (var manager = wts.CreateManager(logger)) {
            Require(Terminate(manager) is Win32Exception { NativeErrorCode: 2 } && manager.HasChildSession,
                "已出现新的 Child Session 时不能把注销失败当作结束成功。");
        }

        Console.WriteLine("CHILD SESSION SELF-TEST PASS: none, live, stale ID, unverifiable, vanished, failed logoff.");

        static Exception? Terminate(ChildSessionManager manager)
        {
            try {
                Task.Run(() => manager.TerminateAsync()).GetAwaiter().GetResult();
                return null;
            } catch (Exception exception) {
                return exception;
            }
        }

        static void Require(bool condition, string failure)
        {
            if (!condition) {
                throw new InvalidOperationException(failure);
            }
        }
    }

    // ReportedId is what WTSGetChildSessionId returns and Sessions what WTSEnumerateSessions lists.
    // Logoff behaves like WTSLogoffSession: a session that does not exist fails with ERROR_FILE_NOT_FOUND.
    private sealed class SimulatedWts
    {
        internal uint? ReportedId { get; set; }
        internal HashSet<uint> Sessions { get; init; } = [0, 1];
        internal bool EnumerationFails { get; init; }
        internal int? LogoffError { get; init; }
        internal Action<SimulatedWts>? DuringLogoff { get; set; }
        internal List<uint> LogoffAttempts { get; } = [];

        internal ChildSessionManager CreateManager(AppLogger logger) =>
            new(logger, () => ReportedId, Enumerate, Logoff);

        private IReadOnlyCollection<uint> Enumerate() => EnumerationFails
            ? throw new Win32Exception(5, "self-test enumeration failure")
            : Sessions.ToArray();

        private uint? Logoff()
        {
            if (ReportedId is not uint sessionId) {
                return null;
            }

            LogoffAttempts.Add(sessionId);
            DuringLogoff?.Invoke(this);
            if (!Sessions.Contains(sessionId)) {
                throw new Win32Exception(2, $"self-test logoff {sessionId} not found");
            }
            if (LogoffError is int error) {
                throw new Win32Exception(error, $"self-test logoff {sessionId} failed");
            }

            Sessions.Remove(sessionId);
            ReportedId = null;
            return sessionId;
        }
    }
}
