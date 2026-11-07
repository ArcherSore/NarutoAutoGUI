using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NarutoAutoGUI.Infrastructure;

// GitHub accepts attachments up to 25 MB, so each volume stays below that and opens on its own.
internal sealed record DiagnosticExportLimits(long VolumeBytes = 24_500_000, long FrameworkLogBytes = 500L << 20,
    long DrawImageBytes = 50L << 20, int KeptExports = 10);

internal sealed record DiagnosticExportResult(string FirstVolume, int Volumes, int Files, int Skipped);

// Failure screenshots come before older logs and annotated images: the first volume is the one most people send.
internal sealed class DiagnosticPackageExporter(AppLogger logger)
{
    private const double TextCompressionEstimate = 0.15;
    private const long EntryOverheadBytes = 128;
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

        try {
            FileStream? output = null;
            ZipArchive? archive = null;
            long centralDirectory = 0;
            var entries = 0;
            void NextVolume()
            {
                archive?.Dispose();
                output?.Dispose();
                output = new FileStream(VolumePath(++volume), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                archive = new ZipArchive(output, ZipArchiveMode.Create);
                centralDirectory = 22;
                entries = 0;
            }

            NextVolume();
            try {
                foreach (var source in Select(applicationDirectory, logDirectory, limits, Skip)) {
                    var nameBytes = Encoding.UTF8.GetByteCount(source.Entry);
                    var estimate = source.Text ? (long)(source.Length * TextCompressionEstimate) : source.Length;
                    if (entries > 0 && output!.Position + centralDirectory + estimate + EntryOverheadBytes
                        + (2 * nameBytes) > limits.VolumeBytes) {
                        NextVolume();
                    }
                    if (source.Text ? TryAddText(archive!, source, Skip) : TryAddBinary(archive!, source, Skip)) {
                        files++;
                        entries++;
                        centralDirectory += 46 + nameBytes;
                    }
                }
            } finally {
                archive?.Dispose();
                output?.Dispose();
            }
        } catch {
            TryDeleteExport(directory);
            throw;
        }
        Prune(exports, directory, limits.KeptExports);
        logger.Info($"诊断包已导出{AppLogger.Field("directory", directory)}{AppLogger.Field("volumes", volume)}"
            + $"{AppLogger.Field("files", files)}{AppLogger.Field("skipped", skipped)}");
        return new DiagnosticExportResult(VolumePath(1), volume, files, skipped);
    }

    private static IEnumerable<Source> Select(string applicationDirectory, string logDirectory,
        DiagnosticExportLimits limits, Action<string, Exception> skip)
    {
        var debug = Path.Combine(applicationDirectory, "debug");
        var debugReadable = IsReadableDirectory(debug, "debug", skip);
        var config = Path.Combine(applicationDirectory, "config");
        var frameworkLog = debugReadable ? Single(Path.Combine(debug, "maafw.log"), "debug/maafw.log") : [];

        // Small, and the only record of a Worker that never reached the GUI: older logs must not push it back a volume.
        var first = Single(Path.Combine(applicationDirectory, "logs", "NarutoAutoWorker.bootstrap.log"),
                "logs/NarutoAutoWorker.bootstrap.log")
            .Concat(Files(logDirectory, "NarutoAutoGUI-*.log", "logs", skip)
                .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => new Source(file.FullName, "logs/" + file.Name, true, file.Length)))
            .Concat(Single(Path.Combine(applicationDirectory, "logs", "updater.log"), "logs/updater.log"))
            .Concat(frameworkLog)
            .Concat(Single(Path.Combine(config, "maanop-config.json"), "config/maanop-config.json"))
            .Concat(Files(config, "maa-*.txt", "config", skip).OrderBy(file => file.Name, StringComparer.Ordinal)
                .Select(file => new Source(file.FullName, "config/" + file.Name, true, file.Length)));
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
            .ThenBy(image => image.Entry, StringComparer.Ordinal).ToArray();
        static bool IsFailure(string entry) => entry.StartsWith("debug/on_error/", StringComparison.Ordinal);
        foreach (var (file, entry) in images.Where(image => IsFailure(image.Entry))) {
            yield return new Source(file.FullName, entry, false, file.Length);
        }

        var frameworkBudget = limits.FrameworkLogBytes - frameworkLog.Sum(source => source.Length);
        foreach (var file in FrameworkLogBackups(debug, skip)) {
            if (file.Length > frameworkBudget) {
                break;
            }
            frameworkBudget -= file.Length;
            yield return new Source(file.FullName, "debug/" + file.Name, true, file.Length);
        }

        var drawBudget = limits.DrawImageBytes;
        foreach (var (file, entry) in images.Where(image => !IsFailure(image.Entry))) {
            if (file.Length > drawBudget) {
                break;
            }
            drawBudget -= file.Length;
            yield return new Source(file.FullName, entry, false, file.Length);
        }
    }

    private static Source[] Single(string path, string entry)
    {
        var file = new FileInfo(path);
        return file.Exists ? [new Source(path, entry, true, file.Length)] : [];
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

    // MaaFramework v5.12.3 / MaaUtils: local timestamp, with 1–3 millisecond digits. The name sorts by time.
    private static IEnumerable<FileInfo> FrameworkLogBackups(string directory, Action<string, Exception> skip) =>
        Files(directory, "maafw.bak.*.log", "debug", skip)
            .Where(file => Regex.IsMatch(file.Name,
                @"\Amaafw\.bak\.[0-9]{4}\.[0-9]{2}\.[0-9]{2}-[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[0-9]{1,3}\.log\z"))
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal);

    private static bool IsReadableDirectory(string directory, string entry, Action<string, Exception> skip)
    {
        try {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) {
                skip(entry, new IOException("诊断采集不跟随目录链接。"));
                return false;
            }
            return true;
        } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            skip(entry, exception);
            return false;
        }
    }

    // A running logger keeps writing, so logs open shared. A read that fails midway leaves the part already read, which
    // still serves diagnosis, and is reported like any other skip.
    private static bool TryAddText(ZipArchive archive, Source source, Action<string, Exception> skip)
    {
        if (TryOpen(source, skip) is not { } input) {
            return false;
        }
        using (input) {
            var profile = AppLogger.UserProfile;
            var account = Path.GetFileName(Path.TrimEndingDirectorySeparator(profile));
            using var reader = new StreamReader(input, new UTF8Encoding(false));
            using var writer = new StreamWriter(
                archive.CreateEntry(source.Entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            try {
                while (reader.ReadLine() is { } line) {
                    // The full match is a regular expression; most lines of a large log never reach it.
                    writer.WriteLine(account.Length > 0 && line.Contains(account, StringComparison.OrdinalIgnoreCase)
                        ? AppLogger.MaskUserProfile(line, profile) : line);
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
    private void Prune(string exports, string current, int kept)
    {
        try {
            var older = Directory.GetDirectories(exports)
                .Where(path => path != current && ExportTimestamp.IsMatch(path))
                .OrderByDescending(path => ExportTimestamp.Match(path).Value, StringComparer.Ordinal)
                .Skip(Math.Max(0, kept - 1));
            foreach (var path in older) {
                TryDeleteExport(path);
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            logger.Warn("旧诊断包清理失败。", exception);
        }
    }
}
