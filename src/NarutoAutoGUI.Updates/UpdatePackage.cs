using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NarutoAutoGUI.Updates;

public static class UpdatePackage
{
    public static readonly IReadOnlyList<string> RequiredFiles = Array.AsReadOnly(new[] {
        "NarutoAutoGUI.exe", "NarutoAutoGUI.dll", "NarutoAutoUpdater.exe", "NarutoAutoUpdater.dll",
        "NarutoAutoUpdater.deps.json", "NarutoAutoUpdater.runtimeconfig.json",
        "worker/NarutoAutoWorker.exe", "worker/NarutoAutoWorker.dll",
        "worker/runtimes/win-x64/native/MaaFramework.dll",
        "worker/runtimes/win-x64/native/MaaWin32ControlUnit.dll", "interface.json", "python/python.exe"
    });

    public static void Extract(string zip, string staging, string version, CancellationToken cancellation)
    {
        if (Directory.Exists(staging) || File.Exists(staging)) {
            throw new IOException("更新暂存目录已存在。");
        }
        RejectLinkedAncestors(Directory.GetParent(Path.GetFullPath(staging))!.FullName);
        Directory.CreateDirectory(staging);
        try {
            using var archive = ZipFile.OpenRead(zip);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries) {
                cancellation.ThrowIfCancellationRequested();
                var relative = entry.FullName.Replace('\\', '/');
                var directory = relative.EndsWith('/');
                var parts = relative.TrimEnd('/').Split('/');
                if (parts.Any(InvalidPart) || (entry.ExternalAttributes & 0x400) != 0
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(relative.TrimEnd('/'))) {
                    throw new InvalidDataException("更新 ZIP 包含非法或冲突路径。");
                }
                var target = Path.GetFullPath(Path.Combine(staging, Path.Combine(parts)));
                if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("更新 ZIP 路径超出暂存目录。");
                }
                if (directory) {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int count;
                while ((count = source.Read(buffer)) != 0) {
                    cancellation.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
            }
            Validate(staging, version);
        } catch {
            Directory.Delete(staging, true);
            throw;
        }
    }

    private static bool InvalidPart(string part) => part.Length == 0 || part is "." or ".."
        || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))
        || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase);

    public static void Validate(string staging, string version)
    {
        RejectLinks(staging);
        foreach (var file in RequiredFiles) {
            if (!File.Exists(Path.Combine(staging, file))) {
                throw new InvalidDataException($"更新包缺少关键文件：{file}");
            }
        }
        foreach (var directory in new[] { "resource", "agent" }) {
            if (!Directory.Exists(Path.Combine(staging, directory))) {
                throw new InvalidDataException($"更新包缺少目录：{directory}");
            }
        }
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "interface.json")));
        if (json.RootElement.GetProperty("name").GetString() != "MaaNOP"
            || SemanticVersion.Parse(json.RootElement.GetProperty("version").GetString()!)
                .CompareTo(SemanticVersion.Parse(version)) != 0) {
            throw new InvalidDataException("更新包产品名或版本与目标 Release 不一致。");
        }
    }

    public static void RejectLinks(string directory)
    {
        RejectLinkedAncestors(directory);
        var info = new DirectoryInfo(directory);
        foreach (var entry in info.EnumerateFileSystemInfos()) {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                throw new IOException("更新目录包含链接或重解析点。");
            }
            if (entry is DirectoryInfo child) {
                RejectLinks(child.FullName);
            }
        }
    }

    private static void RejectLinkedAncestors(string directory)
    {
        var info = new DirectoryInfo(directory);
        for (var parent = info; parent is not null; parent = parent.Parent) {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) {
                throw new IOException("更新目录不能经过链接或重解析点。");
            }
        }
    }
}
