using System.Text;
using System.Windows.Threading;
using NarutoAutoGUI.Protocol;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static readonly TelemetryOptions GuiTelemetryOptions = new(TestDsn, "NarutoAutoGUI@1.0.0+test@1",
        "production", true, 1, 1) { Enabled = true };

    // The GUI reports its own unhandled exceptions — the UI thread's included — in one application Session, never
    // with the profile folder or the machine name, and nothing at all while the gate or the user keeps it off.
    private static void VerifyGuiTelemetry()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var transport = new GuiCapturingTransport();
        using (var telemetry = new GuiTelemetry(transport)) {
            telemetry.Configure(GuiTelemetryOptions);
            telemetry.Configure(GuiTelemetryOptions);
            telemetry.Capture(new InvalidOperationException($"settings failed at {profile}\\config"), "app-domain");
            ThrowOnIsolatedDispatcher(telemetry, $"ui failed at {profile}\\ui");
        }
        var text = transport.Text;
        Require(Count(text, "\"type\":\"event\"") == 2, "GUI 未处理异常（含 UI 线程）应上报。");
        Require(Count(text, "\"init\":true") == 1, "一次 GUI 启动只应有一个应用 Session。");
        Require(text.Contains("ui failed at") && text.Contains("dispatcher"), "UI 线程异常应经 Dispatcher 处理器上报。");
        Require(!text.Contains(profile, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(profile.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
            && !text.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            "上报内容不得包含用户目录或机器名。");

        foreach (var options in new TelemetryOptions?[] {
            null, GuiTelemetryOptions with { Enabled = false }, GuiTelemetryOptions with { Dsn = "not a dsn" } }) {
            var silent = new GuiCapturingTransport();
            using (var telemetry = new GuiTelemetry(silent)) {
                telemetry.Configure(options);
                telemetry.Capture(new InvalidOperationException("must not leave"), "app-domain");
            }
            Require(silent.Text.Length == 0, $"未启用或初始化失败时不得上报任何内容（含 Session）：{options}");
        }

        var switched = new GuiCapturingTransport();
        using (var telemetry = new GuiTelemetry(switched)) {
            telemetry.Configure(GuiTelemetryOptions);
            telemetry.SetEnabled(false);
            telemetry.Capture(new InvalidOperationException("after switching off"), "app-domain");
        }
        Require(!switched.Text.Contains("after switching off"), "关闭“帮助改进软件”后不得再上报。");
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
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            lock (_gate) {
                _text.Append(Encoding.UTF8.GetString(stream.ToArray())).Append('\n');
            }
        }
    }
}
