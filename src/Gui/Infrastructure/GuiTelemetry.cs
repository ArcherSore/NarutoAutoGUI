using System.Windows.Threading;
using Protocol;
using Worker;
using Sentry;
using Sentry.Extensibility;

namespace Gui.Infrastructure;

// The GUI's side of telemetry (ADR 0009): its own unhandled exceptions and the application Session. The SDK runs only
// while the gate allows it and the user keeps it on; switching off closes it, so not even a Session leaves.
internal sealed class GuiTelemetry(ITransport? transport = null) : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly TelemetryScrubber _scrubber = new();
    private TelemetryOptions? _options;
    private TelemetryOptions? _running;
    private IDisposable? _sdk;

    // The options the gate produced for the loaded project; null when there is none or telemetry is not allowed.
    internal void Configure(TelemetryOptions? options)
    {
        lock (_gate) {
            _options = options;
            Apply();
        }
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_gate) {
            _options = _options is null ? null : _options with { Enabled = enabled };
            Apply();
        }
    }

    // The passwords a Run uses; a GUI failure while it runs is scrubbed like the Worker's.
    internal void Register(IReadOnlyList<string> redactionValues) => _scrubber.Add(redactionValues);

    // UI-thread exceptions are not seen by the SDK on its own; the application decides whether they are handled.
    internal void Attach(Dispatcher dispatcher) =>
        dispatcher.UnhandledException += (_, args) => Capture(args.Exception);

    private void Capture(Exception exception)
    {
        try {
            lock (_gate) {
                if (_sdk is not null) {
                    SentrySdk.CaptureException(exception, scope => scope.SetTag("exception.source", "dispatcher"));
                }
            }
        } catch {
            // Telemetry must never fail the handler that reports a failure.
        }
    }

    private void Apply()
    {
        var wanted = _options is { Enabled: true } ? _options : null;
        if (wanted == _running) {
            return;
        }
        Close();
        if (wanted is null) {
            return;
        }
        try {
            _sdk = _scrubber.InitSdk(wanted, transport, _ => { }, () => true, sentry => {
                sentry.TracesSampleRate = 0;
                sentry.AutoSessionTracking = true;
                sentry.ShutdownTimeout = ShutdownTimeout;
            });
            _running = wanted;
        } catch {
            // An invalid configuration only leaves telemetry off.
            Close();
        }
    }

    private void Close()
    {
        try {
            _sdk?.Dispose();
        } catch {
            // Closing waits at most the shutdown timeout and must not keep the application from exiting.
        }
        _sdk = null;
        _running = null;
    }

    public void Dispose()
    {
        lock (_gate) {
            Close();
        }
    }
}
