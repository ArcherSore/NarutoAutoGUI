using System.Text;
using System.Windows.Threading;
using Protocol;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static readonly TelemetryOptions GuiTelemetryOptions = new(TestDsn, "Bunshin@1.0.0+test@1",
        "production", true, 1, 1) { Enabled = true };

    // The GUI reports its own UI-thread exceptions in one application Session, never with the profile folder, the
    // machine name or an IP, and nothing at all while the gate or the user keeps it off.
    private static void VerifyGuiTelemetry()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var transport = new GuiCapturingTransport();
        using (var telemetry = new GuiTelemetry(transport)) {
            telemetry.Configure(GuiTelemetryOptions);
            telemetry.Configure(GuiTelemetryOptions);
            ThrowOnIsolatedDispatcher(telemetry, $"ui failed at {profile}\\ui");
        }
        var text = transport.Text;
        Require(Count(text, "\"type\":\"event\"") == 1 && text.Contains("ui failed at")
            && text.Contains("dispatcher"), "UI 线程未处理异常应经 Dispatcher 处理器上报。");
        Require(Count(text, "\"init\":true") == 1, "重复配置相同选项不得开始新的 Session。");
        Require(!text.Contains(profile, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(profile.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
            && !text.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !text.Contains("ip_address", StringComparison.Ordinal), "上报内容不得包含用户目录、机器名或 IP。");

        TelemetryOptions?[] off = [null, GuiTelemetryOptions with { Enabled = false },
            GuiTelemetryOptions with { Dsn = "not a dsn" }];
        foreach (var options in off) {
            var silent = new GuiCapturingTransport();
            using (var telemetry = new GuiTelemetry(silent)) {
                telemetry.Configure(options);
                ThrowOnIsolatedDispatcher(telemetry, "must not leave");
            }
            Require(silent.Text.Length == 0, $"未启用或初始化失败时不得上报任何内容（含 Session）：{options}");
        }

        var switched = new GuiCapturingTransport();
        using (var telemetry = new GuiTelemetry(switched)) {
            telemetry.Configure(GuiTelemetryOptions);
            telemetry.SetEnabled(false);
            ThrowOnIsolatedDispatcher(telemetry, "after switching off");
        }
        Require(!switched.Text.Contains("after switching off"), "关闭“帮助改进软件”后不得再上报。");

        // A transport that fails changes nothing the application sees: reporting and closing still return.
        using (var telemetry = new GuiTelemetry(new GuiCapturingTransport { Fail = true })) {
            telemetry.Configure(GuiTelemetryOptions);
            ThrowOnIsolatedDispatcher(telemetry, "transport fails");
        }
    }

    // A real Dispatcher on its own thread: its exception is always marked handled, so a failing check cannot end
    // the self-test process.
    private static void ThrowOnIsolatedDispatcher(GuiTelemetry telemetry, string message)
    {
        var done = new ManualResetEventSlim();
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            telemetry.Attach(dispatcher);
            dispatcher.UnhandledException += (_, args) => {
                args.Handled = true;
                done.Set();
            };
            dispatcher.BeginInvoke(() => throw new InvalidOperationException(message));
            dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, dispatcher.InvokeShutdown);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "self-test-dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(done.Wait(TimeSpan.FromSeconds(30)) && thread.Join(TimeSpan.FromSeconds(30)),
            "隔离 Dispatcher 应处理异常并退出。");
    }

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, "").Length) / value.Length;

    private sealed class GuiCapturingTransport : ITransport
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        internal bool Fail { get; init; }

        internal string Text
        {
            get
            {
                lock (_gate) {
                    return _text.ToString();
                }
            }
        }

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            if (Fail) {
                throw new IOException("scripted transport failure");
            }
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            lock (_gate) {
                _text.Append(Encoding.UTF8.GetString(stream.ToArray())).Append('\n');
            }
        }
    }
}
