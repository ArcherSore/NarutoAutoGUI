using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Settings;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyFrameworkPreferences(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "framework-preferences");
        ApplicationSettings Create() => CreateIdleSettings(directory, logger);
        var settings = Create();
        if (settings.CreateFrameworkOptions() != new MaaFrameworkOptions() || Directory.Exists(directory)) {
            throw new InvalidOperationException("框架开关默认值必须为 true/false/false，读取不得写配置。");
        }
        // Older versions saved this choice; a file they left behind must not turn annotated images back on.
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        File.WriteAllText(Path.Combine(directory, "config", "maa-save-draw.txt"), "true");
        Require(!Create().SaveDraw.Value, "标注图不记住：旧版本留下的配置不应在启动时打开它。");
        settings.SaveDraw.Value = true;
        Require(settings.CreateFrameworkOptions().SaveDraw && !Create().SaveDraw.Value,
            "标注图开启后立即生效，重启后恢复关闭。");
        settings.SaveDraw.Value = false;
        File.Delete(Path.Combine(directory, "config", "maa-save-draw.txt"));

        var changes = new List<SettingsToggle>();
        settings.FrameworkOptionsChanged += changes.Add;
        foreach (var toggle in new[] { settings.SaveOnError, settings.SaveDraw, settings.DebugMode }) {
            toggle.Value = !toggle.Value;
            toggle.Value = !toggle.Value;
        }
        Require(changes.Count == 6, "每个框架开关的变化都应通知，才能同步到运行中的 Worker。");
        settings.ClearOnStartup.Value = true;
        Require(changes.Count == 6 && Create().ClearOnStartup.Value, "启动时自动清理应记住，且不是框架选项。");
        settings.ClearOnStartup.Value = false;

        var cases = new (Func<ApplicationSettings, SettingsToggle> Select, string Name, bool Default)[] {
            (s => s.SaveOnError, "save-on-error", true),
            (s => s.DebugMode, "debug-mode", false)
        };
        foreach (var (select, name, defaultValue) in cases) {
            var toggle = select(settings);
            var path = Path.Combine(directory, "config", $"maa-{name}.txt");
            toggle.Value = !defaultValue;
            if (File.ReadAllText(path) != (!defaultValue ? "true" : "false")
                || Create().CreateFrameworkOptions() != settings.CreateFrameworkOptions()) {
                throw new InvalidOperationException($"MaaFramework {name} 必须独立持久化并在重启后恢复。");
            }
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                toggle.Value = defaultValue;
                if (toggle.Value == defaultValue || string.IsNullOrEmpty(toggle.Status)) {
                    throw new InvalidOperationException("框架偏好写入失败必须回滚选择并显示错误。");
                }
            }
            toggle.Value = defaultValue;
            if (toggle.Status != "" || Create().CreateFrameworkOptions() != settings.CreateFrameworkOptions()) {
                throw new InvalidOperationException("框架偏好重试后必须保存成功并清除错误。");
            }
            File.WriteAllText(path, "invalid");
            var reloaded = select(Create());
            if (reloaded.Value != defaultValue || string.IsNullOrEmpty(reloaded.Status)
                || File.ReadAllText(path) != "invalid") {
                throw new InvalidOperationException("无效框架设置必须使用默认值并提示，不能在读取时覆盖配置。");
            }
            File.Delete(path);
        }
    }

    private static void VerifySettingsLog(string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "settings-log");
        using var logger = new AppLogger(Path.Combine(directory, "logs"));
        var written = new List<string>();
        logger.EntryWritten += (_, entry) => written.Add(entry.Message);
        var settings = CreateIdleSettings(directory, logger);
        foreach (var (key, toggle) in settings.Registry.Toggles) {
            written.Clear();
            var value = !toggle.Value;
            toggle.Value = value;
            var expected = $"设置已更改 [key={key}] [value={(value ? "true" : "false")}]";
            Require(written.SequenceEqual([expected]), $"设置变化应写键和新值：{string.Join(" | ", written)}");
        }
        written.Clear();
        settings.DebugMode.Initialize(!settings.DebugMode.Value);
        Require(written.Count == 0, "读取设置不是用户修改，不应写设置变化。");
        using (new FileStream(Path.Combine(directory, "config", "maa-debug-mode.txt"),
            FileMode.Open, FileAccess.Read, FileShare.Read)) {
            settings.DebugMode.Value = !settings.DebugMode.Value;
        }
        Require(written.Count == 1
            && written[0].StartsWith("保存 MaaFramework debug-mode 设置失败", StringComparison.Ordinal),
            $"保存失败只写失败，不能写成已更改：{string.Join(" | ", written)}");
    }
}
