using System.Text.Json;
using NarutoAutoGUI.Models;

namespace NarutoAutoGUI.Infrastructure;

internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly AppLogger _logger;

    internal AppSettingsStore(AppLogger logger, string? settingsPath = null)
    {
        _logger = logger;
        _settingsPath = settingsPath ?? Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "settings.json");
    }

    internal string SettingsPath => _settingsPath;

    internal AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            _logger.Debug($"配置文件尚不存在：{_settingsPath}");
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            using var document = JsonDocument.Parse(json);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? new AppSettings();
            ApplyLegacyGameSettingsMigration(settings, document.RootElement);
            _logger.Info($"已加载配置：{_settingsPath}");
            return settings;
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException)
        {
            _logger.Error("读取配置失败，已使用空配置。", exception);
            return new AppSettings();
        }
    }

    internal void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("配置路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            _logger.Debug($"已保存配置：{_settingsPath}");
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
        {
            _logger.Error("保存配置失败。", exception);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Best-effort cleanup of our own temporary file.
            }
        }
    }

    private void ApplyLegacyGameSettingsMigration(AppSettings settings, JsonElement root)
    {
        var isLegacyGameConfiguration =
            !root.TryGetProperty(nameof(AppSettings.GameArguments), out _);
        if (!isLegacyGameConfiguration)
        {
            return;
        }

        var executableName = Path.GetFileName(settings.GameExecutablePath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(settings.GameExecutablePath)
            && !string.Equals(
                executableName,
                "QQGameLauncher.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        settings.GameExecutablePath = AppSettings.DefaultGameExecutablePath;
        settings.GameArguments = AppSettings.DefaultGameArguments;
        _logger.Info("已将旧版游戏入口迁移为火影忍者 Online 的 Launch.exe + appid 启动配置。");
    }
}
