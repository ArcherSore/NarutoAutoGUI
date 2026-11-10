using System.Diagnostics;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        WorkerHost? host = null;
        try {
            if (args.Length >= 3 && args[0] == "--pretask-test-child") {
                return PretaskSelfTests.RunChild(args);
            }
            if (args.Length == 3 && args[0] == "--framework-diagnostics-self-test") {
                return FrameworkDiagnosticsSelfTest.Run(args[1], args[2]);
            }
            if (args.Length == 2 && args[0] == "--framework-lifecycle-self-test") {
                return await FrameworkLifecycleSelfTest.RunAsync(args[1]);
            }
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) {
                return WorkerSelfTestRunner.Run(args);
            }

            var arguments = WorkerArguments.Parse(args);
            var manifest = LaunchManifestLoader.Load(arguments);
            using var process = Process.GetCurrentProcess();
            using var mutex = new Mutex(
                initiallyOwned: true, $@"Local\NarutoWorker-{process.SessionId}", out var createdNew);
            if (!createdNew) {
                throw new InvalidOperationException(
                    $"Child Session {process.SessionId} 已有 NarutoAutoWorker。 ");
            }

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            host = new WorkerHost(arguments, manifest);
            await host.RunAsync(shutdown.Token);
            return 0;
        } catch (Exception exception) {
            host?.ReportUnhandledException(exception);
            host?.DisposeTelemetry();
            var text = $"[{DateTime.UtcNow:O}] {exception}{Environment.NewLine}";
            Console.Error.Write(text);
            WriteBootstrapLog(text, AppContext.BaseDirectory);
            return 1;
        }
    }

    // A Worker that exits before reaching the GUI has only this file to explain why. It runs from <application>\worker
    // and the GUI's diagnostic package collects <application>\logs.
    internal static void WriteBootstrapLog(string text, string workerDirectory)
    {
        var logs = Path.GetFullPath(Path.Combine(workerDirectory, "..", "logs"));
        foreach (var directory in new[] { logs, Path.GetTempPath() }) {
            try {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, ProtocolConstants.WorkerBootstrapLogFileName), text);
                return;
            } catch {
            }
        }
    }
}
