using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Infrastructure;

// GitHub accepts attachments up to 25 MB, so volumes aim below that and each opens on its own. A file is never split,
// and MaaFramework starts a new log within a million lines, which keeps one log's compressed size within a volume.
internal sealed record DiagnosticExportLimits(long VolumeBytes = 24_500_000, long FrameworkLogBytes = 500L << 20,
    long DrawImageBytes = 50L << 20);

internal sealed record DiagnosticExportResult(string FirstVolume, int Volumes, int Files, int Skipped);

// Failure screenshots come before older logs and annotated images: the first volume is the one most people send.
internal sealed class DiagnosticPackageExporter(AppLogger logger)
{
    private const double TextCompressionEstimate = 0.15;
    private const long EntryOverheadBytes = 128;
    private const long EndOfCentralDirectoryBytes = 22;
    private const int KeptExports = 10;
    private const string TimestampFormat = "yyyyMMdd-HHmmss";
    private static readonly Regex ExportTimestamp = new(@"\d{8}-\d{6}\z", RegexOptions.CultureInvariant);

    private sealed record Source(string Path, string Entry, bool Text, long Length);

    internal DiagnosticExportResult Export(string applicationDirectory, string logDirectory, string? projectName,
        string? projectVersion, DateTime now, DiagnosticExportLimits? limits = null)
    {
        limits ??= new DiagnosticExportLimits();
        var exports = MaaDebugImages.ExportsDirectory(applicationDirectory);
        var name = string.Join("-", new[] { projectName ?? "MaaNOP", "logs", projectVersion,
            now.ToString(TimestampFormat, CultureInfo.InvariantCulture) }.Where(part => !string.IsNullOrEmpty(part)));
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var directory = Path.Combine(exports, name);
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
        Directory.CreateDirectory(directory);
        var skipped = 0;
        var files = 0;
        var volume = 0;
        string VolumePath(int number) => Path.Combine(directory, $"{name}-part{number:D2}.zip");
        void Skip(string entry, Exception exception)
        {
            skipped++;
            logger.Warn($"诊断包跳过文件：{entry}。", exception);
        }

        // The archive owns the stream; the stream is kept only to read how much has been written.
        (FileStream Output, ZipArchive Archive) OpenVolume()
        {
            var stream = new FileStream(VolumePath(++volume), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return (stream, new ZipArchive(stream, ZipArchiveMode.Create));
        }

        try {
            var (output, archive) = OpenVolume();
            var centralDirectory = EndOfCentralDirectoryBytes;
            try {
                foreach (var source in Select(applicationDirectory, logDirectory, limits, Skip)) {
                    var nameBytes = Encoding.UTF8.GetByteCount(source.Entry);
                    var estimate = source.Text ? (long)(source.Length * TextCompressionEstimate) : source.Length;
                    if (output.Position > 0 && output.Position + centralDirectory + estimate + EntryOverheadBytes
                        + (2 * nameBytes) > limits.VolumeBytes) {
                        archive.Dispose();
                        (output, archive) = OpenVolume();
                        centralDirectory = EndOfCentralDirectoryBytes;
                    }
                    if (source.Text ? TryAddText(archive, source, Skip) : TryAddBinary(archive, source, Skip)) {
                        files++;
                        centralDirectory += 46 + nameBytes;
                    }
                }
            } finally {
                archive.Dispose();
            }
        } catch {
            TryDeleteExport(directory);
            throw;
        }
        Prune(exports, directory);
        logger.Info($"诊断包已导出{AppLogger.Field("directory", directory)}{AppLogger.Field("volumes", volume)}"
            + $"{AppLogger.Field("files", files)}{AppLogger.Field("skipped", skipped)}");
        return new DiagnosticExportResult(VolumePath(1), volume, files, skipped);
    }

    private static IEnumerable<Source> Select(string applicationDirectory, string logDirectory,
        DiagnosticExportLimits limits, Action<string, Exception> skip)
    {
        var debug = Path.Combine(applicationDirectory, "debug");
        var debugReadable = MaaDebugImages.TryList(applicationDirectory, debug, skip, out _);
        var config = Path.Combine(applicationDirectory, "config");
        var frameworkLog = debugReadable ? Single(applicationDirectory, "debug/maafw.log") : [];

        // Small, and the only record of a Worker that never reached the GUI: older logs must not push it back a volume.
        var first = Single(applicationDirectory, $"logs/{ProtocolConstants.WorkerBootstrapLogFileName}")
            .Concat(Files(logDirectory, "NarutoAutoGUI-*.log", "logs", skip)
                .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => TextFile(file, "logs")))
            .Concat(Single(applicationDirectory, "logs/updater.log"))
            .Concat(frameworkLog)
            .Concat(Single(applicationDirectory, "config/maanop-config.json"))
            .Concat(Files(config, "maa-*.txt", "config", skip).OrderBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => TextFile(file, "config")));
        foreach (var source in first) {
            yield return source;
        }
        if (!debugReadable) {
            yield break;
        }

        // Evaluated once: enumerating twice could report the same unreadable entry twice.
        var images = MaaDebugImages.Enumerate(applicationDirectory, skip)
            .Select(file => (File: file, Entry: MaaDebugImages.RelativePath(applicationDirectory, file.FullName)))
            .OrderByDescending(image => image.File.LastWriteTimeUtc)
            .ThenBy(image => image.Entry, StringComparer.Ordinal)
            .Select(image => new Source(image.File.FullName, image.Entry, false, image.File.Length)).ToArray();
        static bool IsFailure(Source image) => image.Entry.StartsWith("debug/on_error/", StringComparison.Ordinal);
        var backups = FrameworkLogBackups(debug, skip).Select(file => TextFile(file, "debug"));
        var rest = images.Where(IsFailure)
            .Concat(WithinBudget(backups, limits.FrameworkLogBytes - frameworkLog.Sum(source => source.Length)))
            .Concat(WithinBudget(images.Where(image => !IsFailure(image)), limits.DrawImageBytes));
        foreach (var source in rest) {
            yield return source;
        }
    }

    // Newest first, so the budget ends at the first file that no longer fits.
    private static IEnumerable<Source> WithinBudget(IEnumerable<Source> sources, long budget) =>
        sources.TakeWhile(source => (budget -= source.Length) >= 0);

    private static Source TextFile(FileInfo file, string folder) =>
        new(file.FullName, $"{folder}/{file.Name}", true, file.Length);

    // Under the application directory, where the entry name is also the relative path.
    private static Source[] Single(string applicationDirectory, string entry)
    {
        var file = new FileInfo(Path.Combine(applicationDirectory, entry));
        return file.Exists ? [new Source(file.FullName, entry, true, file.Length)] : [];
    }

    private static FileInfo[] Files(string directory, string pattern, string label, Action<string, Exception> skip)
    {
        try {
            return new DirectoryInfo(directory).GetFiles(pattern);
        } catch (DirectoryNotFoundException) {
            return [];
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            skip($"{label}/{pattern}", exception);
            return [];
        }
    }

    // MaaFramework v5.12.3 / MaaUtils: local timestamp, with 1–3 millisecond digits.
    private static IEnumerable<FileInfo> FrameworkLogBackups(string directory, Action<string, Exception> skip) =>
        Files(directory, "maafw.bak.*.log", "debug", skip)
            .Where(file => Regex.IsMatch(file.Name,
                @"\Amaafw\.bak\.[0-9]{4}\.[0-9]{2}\.[0-9]{2}-[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[0-9]{1,3}\.log\z"))
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal);

    // A running logger keeps writing, so logs open shared. A read that fails midway leaves the part already read, which
    // still serves diagnosis, and is reported like any other skip.
    private static bool TryAddText(ZipArchive archive, Source source, Action<string, Exception> skip)
    {
        if (TryOpen(source, skip) is not { } input) {
            return false;
        }
        using (input) {
            using var reader = new StreamReader(input, new UTF8Encoding(false));
            using var writer = new StreamWriter(
                archive.CreateEntry(source.Entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            try {
                while (reader.ReadLine() is { } line) {
                    writer.WriteLine(AppLogger.MaskUserProfile(line));
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                skip(source.Entry, exception);
            }
        }
        return true;
    }

    // Read whole before the entry exists, so a failed read cannot leave a broken image in the package.
    private static bool TryAddBinary(ZipArchive archive, Source source, Action<string, Exception> skip)
    {
        if (TryOpen(source, skip) is not { } input) {
            return false;
        }
        byte[] bytes;
        using (input) {
            try {
                bytes = new byte[input.Length];
                input.ReadExactly(bytes);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                skip(source.Entry, exception);
                return false;
            }
        }
        using var entry = archive.CreateEntry(source.Entry, CompressionLevel.NoCompression).Open();
        entry.Write(bytes);
        return true;
    }

    private static FileStream? TryOpen(Source source, Action<string, Exception> skip)
    {
        try {
            if (File.GetAttributes(source.Path).HasFlag(FileAttributes.ReparsePoint)) {
                skip(source.Entry, new IOException("诊断采集不跟随文件链接。"));
                return null;
            }
            return new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            skip(source.Entry, exception);
            return null;
        }
    }

    private void TryDeleteExport(string directory)
    {
        try {
            Directory.Delete(directory, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            logger.Warn($"未完成的诊断包目录清理失败：{directory}。", exception);
        }
    }

    // The timestamp in the name orders exports: a directory's modified time moves with backup and sync tools.
    private void Prune(string exports, string current)
    {
        try {
            var older = Directory.GetDirectories(exports)
                .Where(path => path != current && ExportTimestamp.IsMatch(path))
                .OrderByDescending(path => ExportTimestamp.Match(path).Value, StringComparer.Ordinal)
                .Skip(KeptExports - 1);
            foreach (var path in older) {
                TryDeleteExport(path);
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            logger.Warn("旧诊断包清理失败。", exception);
        }
    }
}
