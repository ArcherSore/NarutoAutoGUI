namespace NarutoAutoGUI.Infrastructure;

// MaaFramework images under <application>\debug, shared by diagnostic export and cleanup.
internal static class MaaDebugImages
{
    private static readonly string[] Folders = ["vision", "on_error", "screencap"];

    // Never follows links. onSkip receives unreadable or linked entries relative to applicationDirectory, using '/'.
    internal static IEnumerable<FileInfo> Enumerate(string applicationDirectory, Action<string, Exception> onSkip)
    {
        var debug = Path.Combine(applicationDirectory, "debug");
        if (!TryList(applicationDirectory, debug, onSkip, out _)) {
            yield break;
        }
        foreach (var folder in Folders) {
            var directories = new Stack<string>([Path.Combine(debug, folder)]);
            while (directories.TryPop(out var directory)) {
                if (!TryList(applicationDirectory, directory, onSkip, out var entries)) {
                    continue;
                }
                foreach (var entry in entries) {
                    if (entry is DirectoryInfo child) {
                        directories.Push(child.FullName);
                    } else if (IsImage(folder, entry.Extension)) {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) {
                            onSkip(RelativePath(applicationDirectory, entry.FullName), new IOException("不跟随文件链接。"));
                        } else {
                            yield return (FileInfo)entry;
                        }
                    }
                }
            }
        }
    }

    internal static string RelativePath(string applicationDirectory, string path) =>
        Path.GetRelativePath(applicationDirectory, path).Replace('\\', '/');

    private static bool IsImage(string folder, string extension)
    {
        extension = extension.ToLowerInvariant();
        return folder switch {
            "vision" => extension == ".jpg",
            "on_error" => extension == ".png",
            _ => extension is ".png" or ".jpg" or ".jpeg"
        };
    }

    // Missing directories are expected (debug options may never have produced them) and are not reported.
    private static bool TryList(string applicationDirectory, string directory, Action<string, Exception> onSkip,
        out FileSystemInfo[] entries)
    {
        entries = [];
        try {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) {
                onSkip(RelativePath(applicationDirectory, directory), new IOException("不跟随目录链接。"));
                return false;
            }
            entries = new DirectoryInfo(directory).GetFileSystemInfos();
            return true;
        } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
            return false;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            onSkip(RelativePath(applicationDirectory, directory), exception);
            return false;
        }
    }
}
