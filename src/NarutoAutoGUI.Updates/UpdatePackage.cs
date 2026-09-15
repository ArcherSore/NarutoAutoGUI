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
