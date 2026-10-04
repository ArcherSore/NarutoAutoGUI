using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Settings;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyFrameworkPreferences(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "framework-preferences");
        ApplicationSettings Create()
        {
            return new ApplicationSettings(directory, logger, () => Task.CompletedTask,
                () => Task.CompletedTask, () => { }, () => Task.CompletedTask);
        }
        var settings = Create();
        if (settings.CreateFrameworkOptions() != new MaaFrameworkOptions() || Directory.Exists(directory)) {
            throw new InvalidOperationException("框架开关默认值必须为 true/false/false，读取不得写配置。");
        }
        var cases = new (Func<ApplicationSettings, SettingsToggle> Select, string Name, bool Default)[] {
            (s => s.SaveOnError, "save-on-error", true),
            (s => s.SaveDraw, "save-draw", false),
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
        Console.WriteLine("FRAMEWORK SETTINGS SELF-TEST PASS: defaults, independent persistence and failure rollback.");
    }
}
