using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.Settings;

internal sealed class ApplicationSettings
{
    private readonly string _updatePreferencePath;
    private readonly AppLogger _logger;

    internal ApplicationSettings(string applicationDirectory, AppLogger logger,
        Func<Task> checkUpdate, Func<Task> exportDiagnostics, Action replayOnboarding)
    {
        _updatePreferencePath = Path.Combine(applicationDirectory, "config", "update-check.txt");
        _logger = logger;
        CheckOnStartup = new SettingsToggle(SaveUpdatePreference);
        CheckUpdate = new SettingsAction(checkUpdate);
        ExportDiagnostics = new SettingsAction(exportDiagnostics);
        ReplayOnboarding = new SettingsAction(() => { replayOnboarding(); return Task.CompletedTask; });
        Registry.Toggles.Add("update.checkOnStartup", CheckOnStartup);
        Registry.Actions.Add("update.check", CheckUpdate);
        Registry.Actions.Add("diagnostics.export", ExportDiagnostics);
        Registry.Actions.Add("onboarding.replay", ReplayOnboarding);
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
    internal SettingsAction CheckUpdate { get; }
    internal SettingsAction ExportDiagnostics { get; }
    internal SettingsAction ReplayOnboarding { get; }
    internal SettingsValue CurrentVersion { get; } = new() { Text = "—" };

    internal void LoadUpdatePreference() => CheckOnStartup.Initialize(!File.Exists(_updatePreferencePath)
        || File.ReadAllText(_updatePreferencePath) != "false");

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
