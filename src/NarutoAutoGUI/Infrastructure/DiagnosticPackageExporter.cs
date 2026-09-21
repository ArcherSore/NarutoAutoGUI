using System.IO.Compression;
using System.Text.Json;

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
                // Stage each log before adding an entry, so a failed read cannot leave a partial log in the ZIP.
                using var staging = new FileStream(temporary + ".log", FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 81920, FileOptions.DeleteOnClose);
                foreach (var file in RecentGuiLogs(logDirectory, missing, skipped)) {
                    AddLog(file.FullName, "logs/" + file.Name, selected: true);
                }
                AddLog(Path.Combine(applicationDirectory, "logs", "updater.log"), "logs/updater.log");
                AddLog(Path.Combine(applicationDirectory, "debug", "maafw.log"), "debug/maafw.log");
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

                void AddLog(string path, string entryName, bool selected = false)
                {
                    FileStream source;
                    try {
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
                                Skip(entryName, new EndOfStreamException("日志读取期间被截断。"), skipped);
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
        logger.Warn($"诊断包跳过日志：{entryName}。", exception);
    }
}
