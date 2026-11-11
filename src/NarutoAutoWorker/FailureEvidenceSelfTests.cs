using System.IO.Compression;
using System.Text;

namespace NarutoAutoWorker;

// What a failed Plan Item takes from the debug folder: only its own time window and processes, across rotation.
internal static class FailureEvidenceSelfTests
{
    private const int Worker = 4100;
    private const int Agent = 4200;
    private const int Other = 4300;

    internal static (string Name, Action Body)[] Checks => [
        ("FailureEvidence." + nameof(VerifyFrameworkLogSelection), VerifyFrameworkLogSelection),
        ("FailureEvidence." + nameof(VerifyFrameworkLogBudget), VerifyFrameworkLogBudget),
        ("FailureEvidence." + nameof(VerifyErrorImageSelection), VerifyErrorImageSelection),
        ("FailureEvidence." + nameof(VerifyUnreadableEvidence), VerifyUnreadableEvidence)
    ];

    private static readonly DateTime Start = new(2026, 11, 9, 21, 0, 0, DateTimeKind.Local);
    private static readonly EvidenceWindow Window = new(Start.ToUniversalTime(),
        Start.AddMinutes(10).ToUniversalTime(), [Worker, Agent]);

    private static string Line(DateTime time, int pid, string text) =>
        $"[{time:yyyy-MM-dd HH:mm:ss.fff}][INF][Px{pid}][Tx1][Test] {text}";

    // The window bounds the lines, not a byte offset: a new Agent rotates maafw.log, sometimes inside the window,
    // and lines of another process or of the next Run are never taken.
    private static void VerifyFrameworkLogSelection()
    {
        using var debug = new TemporaryDirectory();
        File.WriteAllLines(Path.Combine(debug.Path, $"maafw.bak.{Start.AddHours(-1):yyyy.MM.dd-HH.mm.ss.fff}.log"), [
            Line(Start.AddHours(-2), Worker, "rotated long before")
        ]);
        File.WriteAllLines(Path.Combine(debug.Path, $"maafw.bak.{Start.AddMinutes(3):yyyy.MM.dd-HH.mm.ss.fff}.log"), [
            Line(Start.AddSeconds(-1), Worker, "before window"),
            Line(Start.AddSeconds(1), Worker, "worker before rotation"),
            "  continuation of worker line",
            Line(Start.AddSeconds(2), Other, "another process"),
            "  continuation of another process"
        ]);
        File.WriteAllLines(Path.Combine(debug.Path, "maafw.log"), [
            Line(Start.AddMinutes(4), Agent, "agent after rotation"),
            Line(Start.AddMinutes(10), Worker, "at the end"),
            Line(Start.AddMinutes(10).AddMilliseconds(1), Worker, "next run")
        ]);
        var (text, status) = FailureEvidence.FrameworkLog(debug.Path, Window);
        var expected = string.Join("\n", Line(Start.AddSeconds(1), Worker, "worker before rotation"),
            "  continuation of worker line", Line(Start.AddMinutes(4), Agent, "agent after rotation"),
            Line(Start.AddMinutes(10), Worker, "at the end"));
        RunSelfTests.Expect(text == expected && status == "captured",
            $"maafw.log 证据应只含时间窗内、Worker 与本项 Agent 的行（含续行与轮转文件）：{text}");
    }

    // At most 1 MiB, the part closest to the failure kept.
    private static void VerifyFrameworkLogBudget()
    {
        using var debug = new TemporaryDirectory();
        var lines = Enumerable.Range(0, 20_000)
            .Select(index => Line(Start.AddMilliseconds(index), Worker, $"{index:D6} {new string('x', 80)}"));
        File.WriteAllLines(Path.Combine(debug.Path, "maafw.log"), lines);
        var (text, _) = FailureEvidence.FrameworkLog(debug.Path, Window);
        RunSelfTests.Expect(Encoding.UTF8.GetByteCount(text) <= FailureEvidence.MaximumLogBytes
            && text.EndsWith($"019999 {new string('x', 80)}", StringComparison.Ordinal) && !text.Contains("000000 "),
            "maafw.log 证据应受 1 MiB 上限约束，并保留最接近失败的部分。");
    }

    // Screenshots written in the window, newest first, within 128 files and 5 MiB.
    private static void VerifyErrorImageSelection()
    {
        using var debug = new TemporaryDirectory();
        var onError = Directory.CreateDirectory(Path.Combine(debug.Path, "on_error")).FullName;
        void Image(string name, DateTime written, int bytes = 16)
        {
            var path = Path.Combine(onError, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTime(path, written);
        }
        Image("before.png", Start.AddSeconds(-1));
        Image("after.png", Start.AddMinutes(11));
        for (var index = 0; index < 130; index++) {
            Image($"in-{index:D3}.png", Start.AddSeconds(index + 1));
        }
        var images = FailureEvidence.ErrorImages(debug.Path, Window);
        var names = ZipNames(images.Zip);
        RunSelfTests.Expect(images is { Count: 128, Status: "truncated" } && names.Length == 128
            && names.Contains("on_error/in-129.png") && !names.Contains("on_error/in-000.png")
            && !names.Any(name => name.Contains("before") || name.Contains("after")),
            "错误截图应只取时间窗内最新的 128 张。");

        using var large = new TemporaryDirectory();
        var largeOnError = Directory.CreateDirectory(Path.Combine(large.Path, "on_error")).FullName;
        File.WriteAllBytes(Path.Combine(largeOnError, "huge.png"), new byte[6 * 1024 * 1024]);
        File.SetLastWriteTime(Path.Combine(largeOnError, "huge.png"), Start.AddSeconds(5));
        File.WriteAllBytes(Path.Combine(largeOnError, "small.png"), new byte[32]);
        File.SetLastWriteTime(Path.Combine(largeOnError, "small.png"), Start.AddSeconds(4));
        var limited = FailureEvidence.ErrorImages(large.Path, Window);
        RunSelfTests.Expect(limited is { Count: 1, Status: "truncated" }
            && ZipNames(limited.Zip).SequenceEqual(["on_error/small.png"]), "单次截图总量不得超过 5 MiB。");

        using var empty = new TemporaryDirectory();
        RunSelfTests.Expect(FailureEvidence.ErrorImages(empty.Path, Window) is { Zip: null, Status: "no_images" },
            "没有截图（例如关闭了保存失败截图）时应标注。");
    }

    // A file another process holds or deletes costs only that file; the evidence step never throws.
    private static void VerifyUnreadableEvidence()
    {
        using var debug = new TemporaryDirectory();
        var log = Path.Combine(debug.Path, "maafw.log");
        File.WriteAllLines(log, [Line(Start.AddSeconds(1), Worker, "held")]);
        using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            RunSelfTests.Expect(FailureEvidence.FrameworkLog(debug.Path, Window) is { Text: "", Status: "unreadable" },
                "被占用的 maafw.log 应标注为不可读，不得抛出。");
        }
        RunSelfTests.Expect(FailureEvidence.FrameworkLog(Path.Combine(debug.Path, "missing"), Window).Status
            == "no_evidence", "调试目录不存在时应标注为无证据。");
    }

    private static string[] ZipNames(byte[]? zip)
    {
        if (zip is null) {
            return [];
        }
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "NarutoEvidence_" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
