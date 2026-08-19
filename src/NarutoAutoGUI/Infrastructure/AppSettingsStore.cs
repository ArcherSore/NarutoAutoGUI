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

    internal string MaaNopConfigPath => Path.Combine(
        Path.GetDirectoryName(_settingsPath)
        ?? throw new InvalidOperationException("配置路径没有父目录。"),
        "maanop-config.json");

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
            var settings = ReadSettings(document.RootElement);
            _logger.Info($"已加载配置：{_settingsPath}");
            return settings;
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException)
        {
            _logger.Error("读取配置失败；原文件保持不变。", exception);
            throw new InvalidDataException(
                "Application Settings 无法解析，必须由用户明确重置后才能继续。",
                exception);
        }
    }

    internal void Save(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GameExecutablePath))
        {
            throw new InvalidDataException("GameExecutablePath 不能为空。");
        }
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.MaaNopProjectDirectory = NormalizeProjectDirectory(
            settings.MaaNopProjectDirectory,
            requireInterface: true);
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("配置路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

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

    private AppSettings ReadSettings(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("settings.json 根节点必须是 object。");
        }

        if (root.TryGetProperty(nameof(AppSettings.SchemaVersion), out var schemaElement))
        {
            if (!schemaElement.TryGetInt32(out var schemaVersion)
                || schemaVersion != AppSettings.CurrentSchemaVersion)
            {
                throw new JsonException(
                    $"不支持 Application Settings SchemaVersion {schemaElement.GetRawText()}。");
            }

            RejectUnknownProperties(
                root,
                [
                    nameof(AppSettings.SchemaVersion),
                    nameof(AppSettings.GameExecutablePath),
                    nameof(AppSettings.GameArguments),
                    nameof(AppSettings.MaaNopProjectDirectory)
                ]);
            return new AppSettings
            {
                SchemaVersion = schemaVersion,
                GameExecutablePath = ReadRequiredString(root, nameof(AppSettings.GameExecutablePath)),
                GameArguments = ReadRequiredString(root, nameof(AppSettings.GameArguments), allowEmpty: true),
                MaaNopProjectDirectory = NormalizeProjectDirectory(
                    ReadRequiredString(root, nameof(AppSettings.MaaNopProjectDirectory), allowEmpty: true),
                    requireInterface: false)
            };
        }

        var settings = new AppSettings
        {
            GameExecutablePath = ReadOptionalString(root, nameof(AppSettings.GameExecutablePath))
                                 ?? string.Empty,
            GameArguments = ReadOptionalString(root, nameof(AppSettings.GameArguments), allowEmpty: true)
                            ?? string.Empty
        };
        ApplyLegacyGameSettingsMigration(settings, root);
        settings.MaaNopProjectDirectory = ReadLegacyProjectDirectory(root);
        return settings;
    }

    private string ReadLegacyProjectDirectory(JsonElement root)
    {
        if (root.TryGetProperty(nameof(AppSettings.MaaNopProjectDirectory), out var projectElement))
        {
            if (projectElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("MaaNopProjectDirectory 必须是 string。");
            }
            return NormalizeProjectDirectory(projectElement.GetString() ?? string.Empty, requireInterface: false);
        }

        var legacyExecutable = ReadOptionalString(root, "MaaNopExecutablePath", allowEmpty: true);
        if (string.IsNullOrWhiteSpace(legacyExecutable))
        {
            return string.Empty;
        }

        string executableDirectory;
        try
        {
            executableDirectory = Path.GetDirectoryName(Path.GetFullPath(legacyExecutable)) ?? string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.Warn($"旧 MaaNOP exe 路径无法迁移：{exception.Message}");
            return string.Empty;
        }

        if (File.Exists(Path.Combine(executableDirectory, "interface.json")))
        {
            _logger.Info("已在内存中将旧 MaaNOP exe 路径迁移为其 Project Directory。");
            return executableDirectory;
        }

        var assetsDirectory = Path.Combine(executableDirectory, "assets");
        if (File.Exists(Path.Combine(assetsDirectory, "interface.json")))
        {
            _logger.Info("已在内存中将旧 MaaNOP exe 路径迁移为 assets Project Directory。");
            return Path.GetFullPath(assetsDirectory);
        }

        _logger.Warn("旧 MaaNOP exe 路径附近没有可迁移的 interface.json；请重新选择 MaaNOP Project Directory。");
        return string.Empty;
    }

    private static string NormalizeProjectDirectory(string value, bool requireInterface)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        if (requireInterface && !File.Exists(Path.Combine(fullPath, "interface.json")))
        {
            throw new DirectoryNotFoundException(
                $"MaaNOP Project Directory 必须直接包含 interface.json：{fullPath}");
        }
        return fullPath;
    }

    private static string ReadRequiredString(JsonElement root, string name, bool allowEmpty = false) =>
        ReadOptionalString(root, name, allowEmpty)
        ?? throw new JsonException($"settings.json 缺少 {name}。");

    private static string? ReadOptionalString(JsonElement root, string name, bool allowEmpty = false)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{name} 必须是 string。");
        }
        var value = element.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"{name} 不能为空。");
        }
        return value;
    }

    private static void RejectUnknownProperties(JsonElement root, IEnumerable<string> allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw new JsonException($"settings.json 包含未知字段 {property.Name}。");
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
