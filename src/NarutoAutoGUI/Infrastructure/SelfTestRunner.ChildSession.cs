using System.ComponentModel;
using System.Reflection;
using System.Windows.Threading;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Models;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyChildSessionEndResponsiveness(AppLogger logger)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var wts = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] };
        using var manager = wts.CreateManager(logger);
        using var releaseLogoff = new ManualResetEventSlim();
        Task? ending = null, queuedEnding = null;
        var responsive = false;
        var serialized = false;
        var notificationsOnUi = true;
        manager.StateChanged += (_, _) => notificationsOnUi &= dispatcher.CheckAccess();
        wts.DuringLogoff = _ => {
            dispatcher.BeginInvoke(new Action(() => {
                responsive = ending is { IsCompleted: false }
                    && manager.Snapshot.State == ChildSessionState.Disconnecting;
                queuedEnding = manager.TerminateAsync();
                serialized = !queuedEnding.IsCompleted && wts.LogoffAttempts.Count == 1;
                releaseLogoff.Set();
            }));
            // The old synchronous implementation times out here, so a blocked Dispatcher fails without hanging.
            releaseLogoff.Wait(TimeSpan.FromSeconds(2));
        };

        ending = manager.TerminateAsync();
        PumpUntil(() => ending.IsCompleted && queuedEnding is { IsCompleted: true }, "结束分身的 UI 响应检查未完成。");
        ending.GetAwaiter().GetResult();
        queuedEnding!.GetAwaiter().GetResult();
        Require(responsive, "等待 Windows 注销期间 UI 应继续处理消息，并保持结束中状态。");
        Require(serialized, "Windows 注销完成前不能释放操作锁或开始第二次注销。");
        Require(notificationsOnUi && manager.Snapshot.State == ChildSessionState.NotRunning,
            "注销完成状态应在 UI 线程发布，且结束后回到未运行。");
    }

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

    // Drives the RDP event handlers as the ActiveX would, without creating the control. With a saved credential,
    // logon failures 0/1/2 leave Windows' logon screen to the user and the attempt ends through the usual events.
    private static void VerifyChildSessionLog(string testDirectory)
    {
        using var logger = new AppLogger(Path.Combine(testDirectory, "child-session-log"));
        var written = new List<LogEntry>();
        logger.EntryWritten += (_, entry) => written.Add(entry);

        // Each real state change is written once, whichever operation caused it.
        using (var manager = new SimulatedWts { ReportedId = 7, Sessions = [0, 1, 7] }.CreateManager(logger)) {
            manager.DetectExistingSession();
            manager.DetectExistingSession();
            Require(written.Count(entry => entry.Message.Contains("[to=Existing] [session=7]")) == 1,
                $"重复的状态不应重复写日志：{string.Join(" | ", written)}");
            var ending = manager.TerminateAsync();
            PumpUntil(() => ending.IsCompleted, "结束 Child Session 未完成。");
            ending.GetAwaiter().GetResult();
            Require(written.Any(entry => entry.Message.Contains("[from=Existing] [to=Disconnecting] [session=7]"))
                && written.Any(entry => entry.Message.Contains("[from=Disconnecting] [to=NotRunning]")),
                $"结束过程应写出每次状态变化：{string.Join(" | ", written)}");
        }

        // The state line says it all, without a from equal to its to or a second line repeating it; a failure's
        // dialog text keeps to one line.
        using (var manager = new SimulatedWts().CreateManager(logger)) {
            written.Clear();
            manager.DetectExistingSession();
            Require(written.Count == 1
                && written[0].Message.StartsWith("Child Session 状态变化 [to=NotRunning]", StringComparison.Ordinal),
                $"状态名没变时不应写 from，也不应重复写：{string.Join(" | ", written)}");
            written.Clear();
            typeof(ChildSessionManager).GetMethod("OnConnectionFailed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(manager,
                [null, new ChildSessionConnectionFailedEventArgs("RDP 连接失败：内部错误\n\n断开原因：1\n扩展原因：0", 1, 0)]);
            Require(written.Any(entry => entry.Message
                == "RDP 状态异常：RDP 连接失败：内部错误 断开原因：1 扩展原因：0；ErrorCode=1；ExtendedErrorCode=0"),
                $"RDP 失败日志应为单行：{string.Join(" | ", written)}");
        }

        // Interim logon events leave a trail, so a logon that times out on Windows's screen can be explained later.
        const int badPassword = 0;
        using (var probe = new RdpEventProbe(savedCredential: true)) {
            var diagnostics = new List<(LogLevel Level, string Message)>();
            probe.Host.Diagnostic += (level, message) => diagnostics.Add((level, message));
            probe.Raise("OnLogonError", badPassword);
            Require(diagnostics.Count == 1 && diagnostics[0].Level == LogLevel.Info && diagnostics[0].Message
                == "RDP 登录阶段事件 [code=0 (0x00000000)] [description=登录凭据无效。] [savedCredential=True]"
                && probe.Failures.Count == 0, $"登录中间事件未写出：{string.Join(" | ", diagnostics)}");
            probe.Raise("OnDisconnected", 2308);
            Require(diagnostics.Count == 1 && probe.Failures.SequenceEqual([2308]), "未报告过的断开应走失败路径。");
            probe.Raise("OnDisconnected", 2308);
            Require(diagnostics.Count == 2 && diagnostics[1].Level == LogLevel.Debug
                && diagnostics[1].Message.StartsWith("RDP 已断开，此前已报告失败 [reason=2308 (0x00000904)]")
                && probe.Failures.SequenceEqual([2308]), "已报告失败后的断开只写诊断，不再报告。");
        }

        // Every normal saved-credential logon raises -2, so it must not read like an error in the log.
        using (var probe = new RdpEventProbe(savedCredential: true)) {
            var diagnostics = new List<string>();
            probe.Host.Diagnostic += (_, message) => diagnostics.Add(message);
            probe.Raise("OnLogonError", -2);
            Require(diagnostics.SequenceEqual(
                ["RDP 登录阶段事件 [code=-2 (0xFFFFFFFE)] [description=Winlogon 正在继续登录。] [savedCredential=True]"])
                && probe.Failures.Count == 0, $"-2 应记录为继续登录：{string.Join(" | ", diagnostics)}");
        }

        // The display query reports failure as data: it only ever adds a line to the log.
        var missing = ChildSessionNativeMethods.TryGetClientDisplay(65000, out var error);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        _ = ChildSessionNativeMethods.TryGetClientDisplay(checked((uint)process.SessionId), out _);
        Require(missing is null && error != 0, "不存在的 Session 应返回不可用和 Win32 错误。");
    }

    private static void VerifyRdpLogonEvents()
    {
        const int badPassword = 0, expiredPassword = 1, otherFailure = 2, logonFailure = unchecked((int)0xC000006D);
        using (var probe = new RdpEventProbe(savedCredential: true)) {
            probe.Raise("OnLogonError", badPassword);
            Require(probe.Failures.Count == 0 && probe.Host.SavedCredentialRejected
                && probe.Host.LastConnectionDiagnostic?.ErrorCode == badPassword,
                "保存的凭据被拒绝后必须保留 Windows 登录界面，不能立即报告失败或断开。");
            probe.Raise("OnLoginComplete");
            Require(probe.Logins == 1 && probe.Failures.Count == 0 && probe.Host.SavedCredentialRejected,
                "手动完成登录后应报告成功，并保留凭据曾被拒绝的记录。");
        }
        using (var probe = new RdpEventProbe(savedCredential: true)) {
            probe.Raise("OnLogonError", expiredPassword);
            probe.Raise("OnLogonError", otherFailure);
            Require(probe.Failures.Count == 0, "密码过期与其他登录失败在 Windows 显示登录界面时不能结束连接。");
            probe.Raise("OnDisconnected", 2308);
            probe.Raise("OnFatalError", 7);
            Require(probe.Failures.SequenceEqual([2308]) && probe.Logins == 0, "随后的断开必须且只能报告一次失败。");
        }
        foreach (var terminal in new[] { logonFailure, -1, -3 }) {
            using var probe = new RdpEventProbe(savedCredential: true);
            probe.Raise("OnLogonError", terminal);
            probe.Raise("OnDisconnected", 2308);
            Require(probe.Failures.SequenceEqual([terminal]), $"登录错误 {terminal} 仍必须立即报告失败。");
        }
        using (var probe = new RdpEventProbe(savedCredential: true)) {
            probe.Raise("OnFatalError", 7);
            Require(probe.Failures.SequenceEqual([7]), "致命错误仍必须立即报告失败。");
        }
        foreach (var code in new[] { badPassword, expiredPassword, otherFailure }) {
            using var probe = new RdpEventProbe(savedCredential: false);
            probe.Raise("OnLogonError", code);
            Require(probe.Failures.SequenceEqual([code]) && !probe.Host.SavedCredentialRejected,
                $"未保存凭据时登录错误 {code} 的处理必须保持不变。");
        }
        using (var probe = new RdpEventProbe(savedCredential: true)) {
            probe.Raise("OnLogonError", badPassword);
            probe.Raise("BeginConnectionAttempt", false);
            Require(!probe.Host.SavedCredentialRejected && probe.Host.LastConnectionDiagnostic is null,
                "新的连接尝试必须清除上一次的凭据拒绝记录。");
            probe.Raise("OnLogonError", badPassword);
            Require(probe.Failures.SequenceEqual([badPassword]), "未提交保存的凭据的新尝试必须按原规则处理。");
        }
    }

    // A connection attempt on a host whose ActiveX is never created; handlers are invoked as the event sink does.
    private sealed class RdpEventProbe : IDisposable
    {
        internal RdpEventProbe(bool savedCredential)
        {
            Host.ConnectionFailed += (_, e) => Failures.Add(e.ErrorCode);
            Host.LoginCompleted += (_, _) => Logins++;
            Raise("BeginConnectionAttempt", savedCredential);
        }

        internal RdpActiveXHost Host { get; } = new();
        internal List<int> Failures { get; } = [];
        internal int Logins { get; private set; }

        internal void Raise(string handler, params object[] args) => typeof(RdpActiveXHost)
            .GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Host, args);

        public void Dispose() => Host.Dispose();
    }

    // ReportedId is what WTSGetChildSessionId returns and Sessions what WTSEnumerateSessions lists.
    // Logoff behaves like WTSLogoffSession: a session that does not exist fails with ERROR_FILE_NOT_FOUND.
    // SessionSurvivesLogoff reports success while WTS keeps reporting the session, which no logoff should hide.
    private sealed class SimulatedWts
    {
        internal uint? ReportedId { get; set; }
        internal HashSet<uint> Sessions { get; init; } = [0, 1];
        internal bool EnumerationFails { get; init; }
        internal int? LogoffError { get; init; }
        internal bool SessionSurvivesLogoff { get; init; }
        internal Action<SimulatedWts>? DuringLogoff { get; set; }
        internal List<uint> LogoffAttempts { get; } = [];

        // Never the real WTS or Windows Credential Manager: what is on this machine must not affect a check.
        internal ChildSessionManager CreateManager(AppLogger logger, ChildSessionCredentialStore? credentials = null) =>
            new(logger, () => ReportedId, Enumerate, Logoff,
                credentials ?? new ChildSessionCredentialStore(logger, new MemoryCredentialVault()));

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
            if (SessionSurvivesLogoff) {
                return sessionId;
            }

            Sessions.Remove(sessionId);
            ReportedId = null;
            return sessionId;
        }
    }
}
