using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NarutoAutoWorker;

// A failed Plan Item's time window and the processes that wrote for it: the Worker and the item's Agent.
internal sealed record EvidenceWindow(DateTime StartedUtc, DateTime EndedUtc, IReadOnlyCollection<int> ProcessIds);

// Selects a failed Plan Item's evidence from the project's debug folder (MaaFramework's LogDir). The window bounds
// everything rather than a byte offset: every new Agent may rotate maafw.log, also in the middle of the window.
// Nothing here throws on a file another process holds or removes; the status says what was found.
internal static partial class FailureEvidence
{
    internal const int MaximumLogBytes = 1024 * 1024;
    internal const long MaximumImageBytes = 5 * 1024 * 1024;
    internal const int MaximumImages = 128;

    internal static (string Text, string Status) FrameworkLog(string debugDirectory, EvidenceWindow window)
    {
        if (!Directory.Exists(debugDirectory)) {
            return ("", "no_evidence");
        }
        var start = window.StartedUtc.ToLocalTime();
        var end = window.EndedUtc.ToLocalTime();
        var lines = new Queue<string>();
        var bytes = 0;
        var unreadable = false;
        foreach (var file in LogFiles(debugDirectory, start)) {
            try {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete));
                var taking = false;
                while (reader.ReadLine() is { } line) {
                    if (LinePrefix().Match(line) is { Success: true } prefix) {
                        var time = DateTime.ParseExact(prefix.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
                        var pid = int.Parse(prefix.Groups["pid"].Value, CultureInfo.InvariantCulture);
                        taking = time >= start && time <= end && window.ProcessIds.Contains(pid);
                    }
                    if (!taking) {
                        continue;
                    }
                    // The part closest to the failure is kept.
                    lines.Enqueue(line);
                    bytes += Encoding.UTF8.GetByteCount(line) + 1;
                    while (bytes - 1 > MaximumLogBytes) {
                        bytes -= Encoding.UTF8.GetByteCount(lines.Dequeue()) + 1;
                    }
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                unreadable = true;
            }
        }
        var status = lines.Count != 0 ? "captured" : unreadable ? "unreadable" : "no_evidence";
        return (string.Join("\n", lines), status);
    }

    // Newest first, within 128 files and 5 MiB; MaaFramework writes them only when SaveOnError is on.
    internal static (byte[]? Zip, int Count, string Status) ErrorImages(string debugDirectory, EvidenceWindow window)
    {
        var folder = Path.Combine(debugDirectory, "on_error");
        FileInfo[] candidates;
        try {
            candidates = Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles()
                    .Where(file => file.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"
                        && file.LastWriteTimeUtc >= window.StartedUtc && file.LastWriteTimeUtc <= window.EndedUtc)
                    .OrderByDescending(file => file.LastWriteTimeUtc).ToArray()
                : [];
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return (null, 0, "unreadable");
        }
        if (candidates.Length == 0) {
            return (null, 0, "no_images");
        }

        using var buffer = new MemoryStream();
        var count = 0;
        long total = 0;
        var skipped = false;
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
            foreach (var file in candidates) {
                if (count == MaximumImages || total + file.Length > MaximumImageBytes) {
                    skipped = true;
                    continue;
                }
                try {
                    var data = File.ReadAllBytes(file.FullName);
                    // Images are already compressed; storing them keeps the work small.
                    using var entry = archive.CreateEntry($"on_error/{file.Name}", CompressionLevel.NoCompression)
                        .Open();
                    entry.Write(data);
                    count++;
                    total += data.Length;
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                    skipped = true;
                }
            }
        }
        return count == 0 ? (null, 0, skipped ? "unreadable" : "no_images")
            : (buffer.ToArray(), count, skipped ? "truncated" : "attached");
    }

    // maafw.bak.<rotation time>.log holds lines from before its rotation, so only one rotated inside the window or
    // later can hold the window's lines; the live maafw.log comes last.
    private static IEnumerable<string> LogFiles(string debugDirectory, DateTime start)
    {
        var rotated = Directory.EnumerateFiles(debugDirectory, "maafw.bak.*.log")
            .Select(path => (Path: path, Match: RotatedName().Match(Path.GetFileName(path))))
            .Where(file => file.Match.Success)
            .Select(file => (file.Path, Time: DateTime.ParseExact(file.Match.Groups["time"].Value,
                "yyyy.MM.dd-HH.mm.ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)))
            .Where(file => file.Time >= start).OrderBy(file => file.Time).Select(file => file.Path);
        var current = Path.Combine(debugDirectory, "maafw.log");
        return File.Exists(current) ? rotated.Append(current) : rotated;
    }

    [GeneratedRegex(@"\A\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]\[[A-Z]+\]\[Px(?<pid>\d+)\]")]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"\Amaafw\.bak\.(?<time>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}\.\d{3})\.log\z")]
    private static partial Regex RotatedName();
}
