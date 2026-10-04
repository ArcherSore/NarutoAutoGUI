using NarutoAutoGUI.Infrastructure;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Settings;

internal sealed class ApplicationSettings
{
    private readonly string _updatePreferencePath;
    private readonly string _closePreferencePath;
    private readonly AppLogger _logger;

    internal ApplicationSettings(string applicationDirectory, AppLogger logger,
        Func<Task> checkUpdate, Func<Task> exportDiagnostics, Action replayOnboarding, Func<Task> openAfdian)
    {
        _updatePreferencePath = Path.Combine(applicationDirectory, "config", "update-check.txt");
        _closePreferencePath = Path.Combine(applicationDirectory, "config", "close-to-tray.txt");
        _logger = logger;
        CloseToTray = new SettingsToggle(SaveClosePreference);
        try {
            CloseToTray.Initialize(!File.Exists(_closePreferencePath)
                || File.ReadAllText(_closePreferencePath) != "false");
        } catch (Exception exception) {
            CloseToTray.Status = "无法读取关闭偏好，已默认使用隐藏到托盘。";
            _logger.Warn("读取关闭窗口偏好失败。", exception);
        }
        CheckOnStartup = new SettingsToggle(SaveUpdatePreference);
        SaveOnError = CreateFrameworkToggle(applicationDirectory, "save-on-error", defaultValue: true);
        SaveDraw = CreateFrameworkToggle(applicationDirectory, "save-draw", defaultValue: false);
        DebugMode = CreateFrameworkToggle(applicationDirectory, "debug-mode", defaultValue: false);
        CheckUpdate = new SettingsAction(checkUpdate);
        ExportDiagnostics = new SettingsAction(exportDiagnostics);
        ReplayOnboarding = new SettingsAction(() => { replayOnboarding(); return Task.CompletedTask; });
        OpenAfdian = new SettingsAction(openAfdian) { ToolTip = "在默认浏览器中打开爱发电赞助页面" };
        Registry.Toggles.Add("update.checkOnStartup", CheckOnStartup);
        Registry.Toggles.Add("application.closeToTray", CloseToTray);
        Registry.Toggles.Add("framework.saveOnError", SaveOnError);
        Registry.Toggles.Add("framework.saveDraw", SaveDraw);
        Registry.Toggles.Add("framework.debugMode", DebugMode);
        Registry.Actions.Add("update.check", CheckUpdate);
        Registry.Actions.Add("diagnostics.export", ExportDiagnostics);
        Registry.Actions.Add("onboarding.replay", ReplayOnboarding);
        Registry.Actions.Add("support.openAfdian", OpenAfdian);
        Registry.Values.Add("update.currentVersion", CurrentVersion);
        try {
            Page = Registry.Bind(SettingsDefinition.Load());
        } catch (Exception exception) {
            _logger.Error("加载内置 Settings Definition 失败（Assets/settings.json）。", exception);
            throw;
        }
    }

    internal SettingsRegistry Registry { get; } = new();
    internal SettingsPageModel Page { get; }
    internal SettingsToggle CheckOnStartup { get; }
    internal SettingsToggle CloseToTray { get; }
    internal SettingsToggle SaveOnError { get; }
    internal SettingsToggle SaveDraw { get; }
    internal SettingsToggle DebugMode { get; }
    internal SettingsAction CheckUpdate { get; }
    internal SettingsAction ExportDiagnostics { get; }
    internal SettingsAction ReplayOnboarding { get; }
    internal SettingsAction OpenAfdian { get; }
    internal SettingsValue CurrentVersion { get; } = new() { Text = "—" };

    internal MaaFrameworkOptions CreateFrameworkOptions() => new(SaveOnError.Value, SaveDraw.Value, DebugMode.Value);

    private SettingsToggle CreateFrameworkToggle(string applicationDirectory, string name, bool defaultValue)
    {
        var path = Path.Combine(applicationDirectory, "config", $"maa-{name}.txt");
        SettingsToggle toggle = null!;
        toggle = new SettingsToggle(value => {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, value ? "true" : "false");
                toggle.Status = string.Empty;
            } catch (Exception exception) {
                toggle.Initialize(!value);
                toggle.Status = "无法保存设置，已恢复原选择，请重试。";
                _logger.Warn($"保存 MaaFramework {name} 设置失败。", exception);
            }
        });
        toggle.Initialize(defaultValue);
        try {
            if (File.Exists(path)) {
                if (!bool.TryParse(File.ReadAllText(path).Trim(), out var value)) {
                    throw new InvalidDataException("开关设置必须为 true 或 false。");
                }
                toggle.Initialize(value);
            }
        } catch (Exception exception) {
            toggle.Status = "无法读取设置，已使用默认值。";
            _logger.Warn($"读取 MaaFramework {name} 设置失败。", exception);
        }
        return toggle;
    }

    internal void LoadUpdatePreference() => CheckOnStartup.Initialize(!File.Exists(_updatePreferencePath)
        || File.ReadAllText(_updatePreferencePath) != "false");

    private void SaveClosePreference(bool value)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_closePreferencePath)!);
            File.WriteAllText(_closePreferencePath, value ? "true" : "false");
            CloseToTray.Status = string.Empty;
        } catch (Exception exception) {
            CloseToTray.Initialize(!value);
            CloseToTray.Status = "无法保存关闭偏好，已恢复原选择，请重试。";
            _logger.Warn("保存关闭窗口偏好失败。", exception);
        }
    }

    private void SaveUpdatePreference(bool value)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_updatePreferencePath)!);
            File.WriteAllText(_updatePreferencePath, value ? "true" : "false");
        } catch (Exception exception) {
            CheckUpdate.Status = "无法保存更新设置，请重试。";
            _logger.Warn("保存更新设置失败。", exception);
        }
    }
}
