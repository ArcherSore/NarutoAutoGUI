using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.Models;

internal sealed record NarutoGameLaunchProfile(string AppId, string Arguments, string ExecutablePath)
{
    internal const string AppIdValue = "1103286479";

    internal const string ArgumentsValue = "-/appid:1103286479";

    internal static NarutoGameLaunchProfile Resolve(string? applicationDataRoot = null)
    {
        var root = applicationDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) {
            throw new InvalidOperationException("无法获取当前用户的 ApplicationData 目录；火影忍者 Online 微端启动器路径无法推导。");
        }

        var executablePath = Path.Combine(root, "Tencent", "QQMicroGameBox", "Launch.exe");
        return new NarutoGameLaunchProfile(AppIdValue, ArgumentsValue, executablePath);
    }

    internal static NarutoGameLaunchProfile ResolveExisting(AppLogger logger, string? applicationDataRoot = null)
    {
        var profile = Resolve(applicationDataRoot);
        if (!File.Exists(profile.ExecutablePath)) {
            logger.Warn($"未检测到火影忍者 Online 微端启动器：{profile.ExecutablePath}");
            throw new FileNotFoundException(
                "未检测到火影忍者 Online 微端启动器。请先通过 QQ 游戏平台安装或启动一次火影忍者 Online。",
                profile.ExecutablePath);
        }
        return profile;
    }
}
