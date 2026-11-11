using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gui.Infrastructure;

// Thrown by a check this machine cannot provide the conditions for, so "not tested" never reads as a pass.
internal sealed class SelfTestSkip(string reason) : Exception(reason);

// Runs every check even after a failure, so one broken check cannot hide the others. Shared with the Worker.
internal static class SelfTestSuite
{
    private static readonly TimeSpan CheckLimit = TimeSpan.FromMinutes(5);

    internal static int Run(string title, IEnumerable<(string Name, Action Body)> checks, string[] args,
        string? environment = null)
    {
        UseUtf8WhenRedirected();
        if (environment is not null) {
            Console.WriteLine(environment);
        }
        var only = OnlyFilter(args);
        var selected = checks
            .Where(check => only is null || check.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0) {
            Console.Error.WriteLine($"{title} FAIL: 没有匹配 --only {only} 的自检。");
            return 1;
        }
        var failed = new List<string>();
        var skipped = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, body) in selected) {
            var timer = Stopwatch.StartNew();
            using var watchdog = new System.Threading.Timer(
                _ => Abort(name), null, CheckLimit, Timeout.InfiniteTimeSpan);
            try {
                body();
                Console.WriteLine($"PASS {name} ({Seconds(timer)}s)");
            } catch (SelfTestSkip skip) {
                skipped++;
                Console.WriteLine($"SKIP {name}: {skip.Message}");
            } catch (Exception exception) {
                failed.Add(name);
                Console.Error.WriteLine($"FAIL {name} ({Seconds(timer)}s): {exception}");
            }
        }
        var summary = $"{selected.Length} 项，{skipped} 项跳过，{Seconds(total)}s";
        if (failed.Count != 0) {
            Console.Error.WriteLine($"{title} FAIL: {failed.Count} 项失败（{summary}）：{string.Join("、", failed)}");
            return 1;
        }
        Console.WriteLine($"{title} PASS: {summary}");
        return 0;
    }

    private static string? OnlyFilter(string[] args)
    {
        var index = Array.IndexOf(args, "--only");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Seconds(Stopwatch timer) =>
        timer.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);

    // A redirected stream otherwise uses the system code page, which scripts reading UTF-8 show as mojibake.
    private static void UseUtf8WhenRedirected()
    {
        var utf8 = new UTF8Encoding(false);
        if (Console.IsOutputRedirected) {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        }
        if (Console.IsErrorRedirected) {
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
    }

    // A hung check cannot be cancelled on its own thread; naming it and ending the process beats waiting forever.
    private static void Abort(string name)
    {
        Console.Error.WriteLine($"FAIL {name}: 超过 {CheckLimit.TotalMinutes} 分钟未结束，终止自检。");
        Process.GetCurrentProcess().Kill();
    }
}
