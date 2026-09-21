using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NarutoAutoGUI.Infrastructure;

internal sealed record DiagnosticMetadata(
    DateTime GeneratedAtUtc, string NarutoAutoGuiVersion, string? ProjectName, string? ProjectVersion,
    string OsVersion, string ProcessArchitecture, string ChildSessionState, string WorkerObservation,
    bool SnapshotFresh, string? WorkerState, string? WorkerVersion, int? ProtocolVersion);

internal sealed class DiagnosticPackageExporter(AppLogger logger)
{
    internal void Export(
        string destination, string applicationDirectory, string logDirectory, DiagnosticMetadata metadata)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
            $".NarutoAutoGUI-diagnostics-{Guid.NewGuid():N}.tmp");
        var included = new List<string>();
        var missing = new List<string>();
        var skipped = new List<string>();
        try {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create)) {
                // Stage each file before adding an entry, so a failed read cannot leave a partial ZIP entry.
                using var staging = new FileStream(temporary + ".entry", FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 81920, FileOptions.DeleteOnClose);
                foreach (var file in RecentGuiLogs(logDirectory, missing, skipped)) {
                    AddFile(file.FullName, "logs/" + file.Name, selected: true);
                }
                AddFile(Path.Combine(applicationDirectory, "logs", "updater.log"), "logs/updater.log");
                var debugDirectory = Path.Combine(applicationDirectory, "debug");
                if (CanReadDebugDirectory(debugDirectory, skipped)) {
                    AddFile(Path.Combine(debugDirectory, "maafw.log"), "debug/maafw.log");
                    foreach (var file in MaaFrameworkLogBackups(debugDirectory, skipped)) {
                        AddFile(file.FullName, "debug/" + file.Name, selected: true);
                    }
                    foreach (var file in MaaFrameworkDebugImages(applicationDirectory, skipped)) {
                        AddFile(file.Path, file.Entry, selected: true);
                    }
                }
                using var json = archive.CreateEntry("diagnostics.json").Open();
                JsonSerializer.Serialize(json, new {
                    metadata.GeneratedAtUtc,
                    metadata.NarutoAutoGuiVersion,
                    Project = new { Name = metadata.ProjectName, Version = metadata.ProjectVersion },
                    System = new { metadata.OsVersion, metadata.ProcessArchitecture },
                    Runtime = new {
                        metadata.ChildSessionState, metadata.WorkerObservation, metadata.SnapshotFresh,
                        metadata.WorkerState, metadata.WorkerVersion, metadata.ProtocolVersion
                    },
                    IncludedFiles = included, MissingOptionalFiles = missing, SkippedFiles = skipped
                }, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
                });

                void AddFile(string path, string entryName, bool selected = false)
                {
                    FileStream source;
                    try {
                        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) {
                            Skip(entryName, new IOException("诊断采集不跟随文件链接。"), skipped);
                            return;
                        }
                        source = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                    } catch (Exception exception) when (
                        exception is FileNotFoundException or DirectoryNotFoundException) {
                        missing.Add(entryName);
                        if (selected) {
                            Skip(entryName, exception, skipped);
                        }
                        return;
                    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                        Skip(entryName, exception, skipped);
                        return;
                    }
                    using (source) {
                        staging.SetLength(0);
                        staging.Position = 0;
                        var buffer = new byte[81920];
                        // Bound the read to the size at open; a running logger may keep appending.
                        long remaining;
                        try {
                            remaining = source.Length;
                        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                            Skip(entryName, exception, skipped);
                            return;
                        }
                        while (remaining > 0) {
                            int read;
                            try {
                                read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            } catch (Exception exception) when (
                                exception is IOException or UnauthorizedAccessException) {
                                Skip(entryName, exception, skipped);
                                return;
                            }
                            if (read == 0) {
                                Skip(entryName, new EndOfStreamException("文件读取期间被截断。"), skipped);
                                return;
                            }
                            staging.Write(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                    staging.Position = 0;
                    using var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                    staging.CopyTo(entry);
                    included.Add(entryName);
                }
            }
            File.Move(temporary, destination, overwrite: true);
        } finally {
            try {
                File.Delete(temporary);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                logger.Warn("诊断包临时文件清理失败。", exception);
            }
        }
    }

    private IReadOnlyList<FileInfo> RecentGuiLogs(string directory, List<string> missing, List<string> skipped)
    {
        try {
            return new DirectoryInfo(directory).GetFiles("NarutoAutoGUI-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal)
                .Take(5).ToArray();
        } catch (DirectoryNotFoundException) {
            missing.Add("logs/NarutoAutoGUI-*.log");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Skip("logs/NarutoAutoGUI-*.log", exception, skipped);
        }
        return [];
    }

    private void Skip(string entryName, Exception exception, List<string> skipped)
    {
        skipped.Add(entryName);
        logger.Warn($"诊断包跳过文件：{entryName}。", exception);
    }

    private IReadOnlyList<FileInfo> MaaFrameworkLogBackups(string directory, List<string> skipped)
    {
        try {
            // MaaFramework v5.12.3 / MaaUtils: local timestamp, with 1–3 millisecond digits.
            return new DirectoryInfo(directory).GetFiles("maafw.bak.*.log")
                .Where(file => Regex.IsMatch(file.Name,
                    @"\Amaafw\.bak\.[0-9]{4}\.[0-9]{2}\.[0-9]{2}-[0-9]{2}\.[0-9]{2}\.[0-9]{2}\.[0-9]{1,3}\.log\z"))
                .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        } catch (DirectoryNotFoundException) {
            return [];
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Skip("debug/maafw.bak.*.log", exception, skipped);
            return [];
        }
    }

    private bool CanReadDebugDirectory(string directory, List<string> skipped)
    {
        try {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) {
                Skip("debug", new IOException("诊断采集不跟随目录链接。"), skipped);
                return false;
            }
            return true;
        } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Skip("debug", exception, skipped);
            return false;
        }
    }

    private IReadOnlyList<(string Path, string Entry)> MaaFrameworkDebugImages(
        string applicationDirectory, List<string> skipped)
    {
        var files = new List<(string Path, string Entry)>();
        foreach (var folder in new[] { "vision", "on_error", "screencap" }) {
            var directories = new Stack<DirectoryInfo>();
            directories.Push(new DirectoryInfo(Path.Combine(applicationDirectory, "debug", folder)));
            while (directories.TryPop(out var directory)) {
                var relative = Path.GetRelativePath(applicationDirectory, directory.FullName).Replace('\\', '/');
                FileSystemInfo[] entries;
                try {
                    if (File.GetAttributes(directory.FullName).HasFlag(FileAttributes.ReparsePoint)) {
                        Skip(relative, new IOException("诊断采集不跟随目录链接。"), skipped);
                        continue;
                    }
                    entries = directory.GetFileSystemInfos();
                } catch (Exception exception) when (
                    exception is FileNotFoundException or DirectoryNotFoundException) {
                    // Debug options may never have produced this directory.
                    continue;
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                    Skip(relative, exception, skipped);
                    continue;
                }
                foreach (var entry in entries) {
                    if (entry is DirectoryInfo child) {
                        directories.Push(child);
                        continue;
                    }
                    var extension = entry.Extension.ToLowerInvariant();
                    var supported = folder switch {
                        "vision" => extension == ".jpg",
                        "on_error" => extension == ".png",
                        _ => extension is ".png" or ".jpg" or ".jpeg"
                    };
                    if (!supported) {
                        continue;
                    }
                    var name = Path.GetRelativePath(applicationDirectory, entry.FullName).Replace('\\', '/');
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) {
                        Skip(name, new IOException("诊断采集不跟随文件链接。"), skipped);
                        continue;
                    }
                    files.Add((entry.FullName, name));
                }
            }
        }
        return files.OrderBy(file => file.Entry, StringComparer.Ordinal).ToArray();
    }
}
