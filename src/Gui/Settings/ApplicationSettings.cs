using System.Windows.Media;
using Gui.Infrastructure;
using ProjectModel;
using Protocol;

namespace Gui.Settings;

internal sealed class ApplicationSettings
{
    private readonly string _updatePreferencePath;
    private readonly string _closePreferencePath;
    private readonly string _autoRunConfigurationPath;
    private readonly AutostartTask _autostart;
    private readonly AppLogger _logger;
    private string? _autoRunConfigurationId;

    internal ApplicationSettings(string applicationDirectory, AppLogger logger,
        Func<Task> checkUpdate, Func<Task> exportDiagnostics, Action replayOnboarding, Func<Task> openAfdian,
        Func<Task> manageChildSessionCredentials, Func<Task> clearDebugFiles, Action viewWelcome,
        Action<Uri> openLink, Action<string> readLicense, AutostartTask? autostart = null)
    {
        _updatePreferencePath = Path.Combine(applicationDirectory, "config", "update-check.txt");
        _closePreferencePath = Path.Combine(applicationDirectory, "config", "close-to-tray.txt");
        _autoRunConfigurationPath = Path.Combine(applicationDirectory, "config", "autorun-configuration.txt");
        _autostart = autostart ?? AutostartTask.ForCurrentProcess();
        _logger = logger;
        CloseToTray = new SettingsToggle(SaveClosePreference);
        try {
            CloseToTray.Initialize(!File.Exists(_closePreferencePath)
                || File.ReadAllText(_closePreferencePath) != "false");
        } catch (Exception exception) {
            CloseToTray.Status = "无法读取关闭偏好，已默认使用隐藏到托盘。";
            _logger.Warn("读取关闭窗口偏好失败。", exception);
        }
        Autostart = new SettingsToggle(SaveAutostart);
        try {
            Autostart.Initialize(_autostart.IsEnabled());
        } catch (Exception exception) {
            Autostart.Initialize(false);
            Autostart.Status = "无法读取开机自启动状态。";
            _logger.Warn("读取开机自启动状态失败。", exception);
        }
        AutoRunConfiguration = new SettingsChoice("不自动执行", SaveAutoRunConfiguration);
        AutoRunOnManualLaunch = CreatePersistedToggle(
            Path.Combine(applicationDirectory, "config", "autorun-on-launch.txt"),
            "autorun.onManualLaunch", "手动启动时也自动执行", false, null);
        ReadAutoRunConfiguration();
        AutoRunOnManualLaunch.IsEnabled = _autoRunConfigurationId is not null;
        CheckOnStartup = new SettingsToggle(SaveUpdatePreference);
        SaveOnError = CreateFrameworkToggle(applicationDirectory, "framework.saveOnError", "save-on-error", true);
        // Annotated images fill a disk quickly and nothing caps them, so each launch starts with them off.
        SaveDraw = new SettingsToggle(value => {
            LogToggleChanged("framework.saveDraw", value);
            FrameworkOptionsChanged?.Invoke(SaveDraw!);
        });
        SaveDraw.Initialize(false);
        DebugMode = CreateFrameworkToggle(applicationDirectory, "framework.debugMode", "debug-mode", false);
        // A running Worker learns this choice through the same path as the framework switches.
        HelpImprove = CreatePersistedToggle(Path.Combine(applicationDirectory, "config", "help-improve.txt"),
            "telemetry.helpImprove", "帮助改进软件", true, toggle => FrameworkOptionsChanged?.Invoke(toggle));
        ClearOnStartup = CreatePersistedToggle(Path.Combine(applicationDirectory, "config", "clear-on-startup.txt"),
            "diagnostics.clearOnStartup", "启动时自动清理", false, null);
        CheckUpdate = new SettingsAction(checkUpdate);
        ExportDiagnostics = new SettingsAction(exportDiagnostics);
        ClearDebugFiles = new SettingsAction(clearDebugFiles);
        ReplayOnboarding = new SettingsAction(() => { replayOnboarding(); return Task.CompletedTask; });
        ViewWelcome = new SettingsAction(() => { viewWelcome(); return Task.CompletedTask; }) { IsVisible = false };
        OpenAfdian = new SettingsAction(openAfdian) { ToolTip = "在默认浏览器中打开爱发电赞助页面" };
        ChildSessionCredentials = new SettingsAction(manageChildSessionCredentials);
        Registry.About.OpenLink = openLink;
        OpenRepository = new SettingsAction(() => {
            if (Registry.About.Content?.Github is { } url) { Registry.About.OpenLink(url); }
            return Task.CompletedTask;
        }) { IsVisible = false };
        ViewLicense = new SettingsAction(() => {
            if (Registry.About.Content?.License is { Url: { } url }) {
                Registry.About.OpenLink(url);
            } else if (Registry.About.Content?.License?.Markdown is { } license) {
                readLicense(license);
            }
            return Task.CompletedTask;
        }) { IsVisible = false };
        Registry.Toggles.Add("update.checkOnStartup", CheckOnStartup);
        Registry.Toggles.Add("application.closeToTray", CloseToTray);
        Registry.Toggles.Add("application.autostart", Autostart);
        Registry.Toggles.Add("autorun.onManualLaunch", AutoRunOnManualLaunch);
        Registry.Choices.Add("autorun.configuration", AutoRunConfiguration);
        Registry.Toggles.Add("framework.saveOnError", SaveOnError);
        Registry.Toggles.Add("framework.saveDraw", SaveDraw);
        Registry.Toggles.Add("framework.debugMode", DebugMode);
        Registry.Toggles.Add("diagnostics.clearOnStartup", ClearOnStartup);
        Registry.Toggles.Add("telemetry.helpImprove", HelpImprove);
        Registry.Actions.Add("update.check", CheckUpdate);
        Registry.Actions.Add("diagnostics.export", ExportDiagnostics);
        Registry.Actions.Add("diagnostics.clear", ClearDebugFiles);
        Registry.Actions.Add("onboarding.replay", ReplayOnboarding);
        Registry.Actions.Add("welcome.view", ViewWelcome);
        Registry.Actions.Add("support.openAfdian", OpenAfdian);
        Registry.Actions.Add("project.openRepository", OpenRepository);
        Registry.Actions.Add("project.viewLicense", ViewLicense);
        Registry.Actions.Add("childSession.credentials", ChildSessionCredentials);
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
    internal SettingsToggle Autostart { get; }
    internal SettingsChoice AutoRunConfiguration { get; }
    internal SettingsToggle AutoRunOnManualLaunch { get; }
    internal SettingsToggle SaveOnError { get; }
    internal SettingsToggle SaveDraw { get; }
    internal SettingsToggle DebugMode { get; }
    internal SettingsToggle ClearOnStartup { get; }
    internal SettingsToggle HelpImprove { get; }
    internal SettingsAction CheckUpdate { get; }
    internal SettingsAction ExportDiagnostics { get; }
    internal SettingsAction ClearDebugFiles { get; }
    internal SettingsAction ReplayOnboarding { get; }
    internal SettingsAction ViewWelcome { get; }
    internal SettingsAction OpenAfdian { get; }
    internal SettingsAction ChildSessionCredentials { get; }
    internal SettingsAction OpenRepository { get; }
    internal SettingsAction ViewLicense { get; }
    internal SettingsValue CurrentVersion { get; } = new() { Text = "—" };

    internal event Action<SettingsToggle>? FrameworkOptionsChanged;

    internal void ShowProjectAbout(ProjectAbout? about, ImageSource? icon)
    {
        Registry.About.Content = about;
        Registry.About.Icon = icon;
        OpenRepository.IsVisible = about?.Github is not null;
        OpenRepository.ToolTip = about?.Github?.AbsoluteUri;
        ViewLicense.IsVisible = about?.License is not null;
        ViewLicense.IsEnabled = about?.License?.Error is null;
        ViewLicense.Status = about?.License?.Error ?? "";
        ViewLicense.ToolTip = about?.License?.Url?.AbsoluteUri;
    }

    internal Guid? AutoRunConfigurationId => Guid.TryParse(_autoRunConfigurationId, out var id) ? id : null;

    // The choice is stored by configuration id, so renaming keeps it. One that no longer exists falls back to "none"
    // together with the manual-launch switch, quietly: there is nothing for the user to act on.
    // authoritative is false while the configuration file cannot be read: the list is then a placeholder, and the
    // chosen configuration is kept for when the file can be read again instead of being reset as deleted.
    internal void UpdateAutoRunConfigurations(IReadOnlyList<AutoRunTarget> configurations, bool authoritative = true)
    {
        var options = configurations.Select(item => new SettingsChoiceOption(item.Id.ToString(), item.Name)).ToArray();
        if (authoritative && _autoRunConfigurationId is not null
            && options.All(option => option.Id != _autoRunConfigurationId)) {
            try {
                SaveAutoRunConfiguration(null);
                AutoRunOnManualLaunch.Value = false;
            } catch (Exception) {
                // Already logged by the save; the stale id is retried on the next launch.
            }
        }
        AutoRunConfiguration.SetOptions(options, _autoRunConfigurationId);
    }

    // The choice is kept while the gate blocks telemetry, so it is unavailable rather than switched off.
    internal void ShowTelemetryAvailability(bool available) => HelpImprove.IsEnabled = available;

    internal MaaFrameworkOptions CreateFrameworkOptions() => new(SaveOnError.Value, SaveDraw.Value, DebugMode.Value);

    internal void LogToggleChanged(string key, bool value) => _logger.Info(
        $"设置已更改{AppLogger.Field("key", key)}{AppLogger.Field("value", value ? "true" : "false")}");

    private SettingsToggle CreateFrameworkToggle(
        string applicationDirectory, string key, string name, bool defaultValue) => CreatePersistedToggle(
            Path.Combine(applicationDirectory, "config", $"maa-{name}.txt"), key, $"MaaFramework {name}", defaultValue,
            toggle => FrameworkOptionsChanged?.Invoke(toggle));

    private SettingsToggle CreatePersistedToggle(
        string path, string key, string label, bool defaultValue, Action<SettingsToggle>? changed)
    {
        SettingsToggle toggle = null!;
        toggle = new SettingsToggle(value => {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, value ? "true" : "false");
                toggle.Status = string.Empty;
                LogToggleChanged(key, value);
                changed?.Invoke(toggle);
            } catch (Exception exception) {
                toggle.Initialize(!value);
                toggle.Status = "无法保存设置，已恢复原选择，请重试。";
                _logger.Warn($"保存 {label} 设置失败。", exception);
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
            _logger.Warn($"读取 {label} 设置失败。", exception);
        }
        return toggle;
    }

    internal void LoadUpdatePreference() => CheckOnStartup.Initialize(!File.Exists(_updatePreferencePath)
        || File.ReadAllText(_updatePreferencePath) != "false");

    private void SaveAutostart(bool value)
    {
        try {
            if (value) {
                _autostart.Enable();
            } else {
                _autostart.Disable();
            }
            Autostart.Status = string.Empty;
            LogToggleChanged("application.autostart", value);
        } catch (Exception exception) {
            Autostart.Initialize(!value);
            Autostart.Status = "无法设置开机自启动，已恢复原选择，请重试。";
            _logger.Warn("设置开机自启动失败。", exception);
        }
    }

    private void ReadAutoRunConfiguration()
    {
        try {
            if (File.Exists(_autoRunConfigurationPath)) {
                _autoRunConfigurationId = Guid.Parse(File.ReadAllText(_autoRunConfigurationPath).Trim()).ToString();
            }
        } catch (Exception exception) {
            AutoRunConfiguration.Status = "无法读取设置，已使用默认值。";
            _logger.Warn("读取自动执行配置失败。", exception);
        }
    }

    // Throws when the choice cannot be stored, so that the choice control restores the previous selection.
    private void SaveAutoRunConfiguration(string? id)
    {
        try {
            if (id is null) {
                File.Delete(_autoRunConfigurationPath);
            } else {
                Directory.CreateDirectory(Path.GetDirectoryName(_autoRunConfigurationPath)!);
                File.WriteAllText(_autoRunConfigurationPath, id);
            }
        } catch (Exception exception) {
            _logger.Warn("保存自动执行配置失败。", exception);
            throw;
        }
        _autoRunConfigurationId = id;
        AutoRunOnManualLaunch.IsEnabled = id is not null;
        var name = AutoRunConfiguration.Options.FirstOrDefault(option => option.Id == id)?.Name;
        _logger.Info($"设置已更改{AppLogger.Field("key", "autorun.configuration")}"
            + $"{AppLogger.Field("value", id ?? "none")}{AppLogger.Field("name", name ?? string.Empty)}");
    }

    private void SaveClosePreference(bool value)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_closePreferencePath)!);
            File.WriteAllText(_closePreferencePath, value ? "true" : "false");
            CloseToTray.Status = string.Empty;
            LogToggleChanged("application.closeToTray", value);
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
            LogToggleChanged("update.checkOnStartup", value);
        } catch (Exception exception) {
            CheckUpdate.Status = "无法保存更新设置，请重试。";
            _logger.Warn("保存更新设置失败。", exception);
        }
    }
}
