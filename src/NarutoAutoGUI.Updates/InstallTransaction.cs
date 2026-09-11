namespace NarutoAutoGUI.Updates;

public sealed class InstallTransaction(Action<string> log, Action<string, string>? moveDirectory = null)
{
    public void Install(string installation, string staging, string version)
    {
        installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation));
        staging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging));
        var parent = Directory.GetParent(installation)?.FullName;
        if (parent is null || Directory.GetParent(staging)?.FullName != parent
            || !Path.GetFileName(staging).StartsWith(Path.GetFileName(installation) + ".update-",
                StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("安装目录与暂存目录关系无效。");
        }
        UpdatePackage.Validate(staging, version);
        UpdatePackage.RejectLinks(installation);
        EnsureFilesReleased(installation);
        var backup = installation + ".old";
        if (Directory.Exists(backup)) {
            UpdatePackage.RejectLinks(backup);
            Directory.Delete(backup, true);
            log("已清理上次更新备份。");
        }
        foreach (var name in new[] { "config", "logs" }) {
            var source = Path.Combine(installation, name);
            var target = Path.Combine(staging, name);
            if (File.Exists(target)) {
                throw new InvalidDataException("更新包的用户数据目录类型错误。");
            }
            if (Directory.Exists(target)) {
                Directory.Delete(target, true);
            }
            if (Directory.Exists(source)) {
                CopyTree(source, target);
            }
        }
        // Admission is a runtime journal, never a transferable user configuration.
        var state = Path.Combine(staging, "state");
        if (Directory.Exists(state)) {
            Directory.Delete(state, true);
        }
        EnsureFilesReleased(installation);
        log("安装开始：正在替换完整目录。");
        var move = moveDirectory ?? Directory.Move;
        move(installation, backup);
        try {
            move(staging, installation);
            log("目录替换成功；旧备份保留到下一次更新。");
        } catch (Exception installError) {
            log("目录替换失败，RollingBack。");
            try {
                move(backup, installation);
                log("Rollback 成功，已恢复旧版。");
            } catch (Exception rollbackError) {
                log($"Rollback 失败：{rollbackError.Message}；保留备份={backup}");
                throw new AggregateException("更新失败且无法自动恢复，请保留旧备份。", installError, rollbackError);
            }
            throw;
        }
    }

    public static void EnsureFilesReleased(string installation)
    {
        UpdatePackage.RejectLinks(installation);
        foreach (var file in Directory.EnumerateFiles(installation, "*", SearchOption.AllDirectories)) {
            using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
        }
        foreach (var child in Directory.EnumerateDirectories(source)) {
            CopyTree(child, Path.Combine(target, Path.GetFileName(child)));
        }
    }
}
