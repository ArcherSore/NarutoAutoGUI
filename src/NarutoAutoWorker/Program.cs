using System.Diagnostics;

namespace NarutoAutoWorker;

internal static class Program
{
    private static readonly string BootstrapLogPath = Path.Combine(
        Path.GetTempPath(),
        "NarutoAutoWorker.bootstrap.log");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var arguments = WorkerArguments.Parse(args);
            var manifest = LaunchManifestLoader.Load(arguments);
            using var process = Process.GetCurrentProcess();
            using var mutex = new Mutex(
                initiallyOwned: true,
                $@"Local\NarutoAutoWorker-{process.SessionId}",
                out var createdNew);
            if (!createdNew)
            {
                throw new InvalidOperationException(
                    $"Child Session {process.SessionId} 已有 NarutoAutoWorker。 ");
            }

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            var host = new WorkerHost(arguments, manifest);
            await host.RunAsync(shutdown.Token);
            return 0;
        }
        catch (Exception exception)
        {
            var text = $"[{DateTime.UtcNow:O}] {exception}{Environment.NewLine}";
            Console.Error.Write(text);
            try
            {
                File.AppendAllText(BootstrapLogPath, text);
            }
            catch
            {
            }
            return 1;
        }
    }
}
