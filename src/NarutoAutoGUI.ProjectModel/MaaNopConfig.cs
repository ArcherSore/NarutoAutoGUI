using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NarutoAutoGUI.ProjectModel;

public sealed record MaaNopConfig
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid ActiveConfigurationId { get; init; }
    public required IReadOnlyList<TaskConfiguration> Configurations { get; init; }
}

public sealed record TaskConfiguration
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> SelectedTasks { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> ExplicitOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

internal sealed class MaaNopConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private bool _protectOriginal;
    private bool _originalBackedUp;
    internal string? LoadWarning { get; private set; }
    internal bool WasMissing { get; private set; }

    internal MaaNopConfigStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    internal MaaNopConfig Load()
    {
        MaaNopConfig config;
        bool needsSave;
        try {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema)) {
                throw new InvalidDataException("MaaNOP Config 缺少合法的 SchemaVersion。");
            }
            if (schema == 1) {
                var legacy = root.Deserialize<LegacyConfig>(JsonOptions)
                    ?? throw new InvalidDataException("MaaNOP Config 为空。");
                var migrated = new TaskConfiguration {
                    Id = Guid.NewGuid(), Name = "配置 1",
                    SelectedTasks = legacy.SelectedTasks, ExplicitOptions = legacy.ExplicitOptions
                };
                config = new MaaNopConfig { ActiveConfigurationId = migrated.Id, Configurations = [migrated] };
                needsSave = true;
            } else if (schema == MaaNopConfig.CurrentSchemaVersion) {
                var active = root.TryGetProperty("ActiveConfigurationId", out var pointer)
                    && pointer.ValueKind == JsonValueKind.String && pointer.TryGetGuid(out var id) ? id : Guid.Empty;
                var node = JsonNode.Parse(root.GetRawText())!;
                node["ActiveConfigurationId"] = active;
                config = node.Deserialize<MaaNopConfig>(JsonOptions)
                    ?? throw new InvalidDataException("MaaNOP Config 为空。");
                Validate(config, requireActive: false);
                if (config.Configurations.Count == 0) {
                    config = CreateEmpty();
                    needsSave = true;
                } else {
                    needsSave = !config.Configurations.Any(item => item.Id == active);
                    if (needsSave) {
                        config = config with { ActiveConfigurationId = config.Configurations[0].Id };
                    }
                }
            } else {
                throw new InvalidDataException($"不支持 MaaNOP Config SchemaVersion {schema}。");
            }
            Validate(config);
        } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
            WasMissing = true;
            return CreateEmpty();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException) {
            _protectOriginal = true;
            LoadWarning = $"无法读取任务配置，已提供空配置。首次保存前将保留原文件。{exception.Message}";
            return CreateEmpty();
        }
        if (needsSave) {
            try {
                Save(config);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                LoadWarning = $"配置已加载，但未能保存迁移或激活位置，原文件保持不变。{exception.Message}";
            }
        }
        return config;
    }

    private static MaaNopConfig CreateEmpty()
    {
        var empty = new TaskConfiguration { Id = Guid.NewGuid(), Name = "配置 1" };
        return new MaaNopConfig { ActiveConfigurationId = empty.Id, Configurations = [empty] };
    }

    internal void Save(MaaNopConfig config)
    {
        Validate(config);
        if (_protectOriginal && !_originalBackedUp) {
            var backup = Path.Combine(Path.GetDirectoryName(_path)!,
                $"{Path.GetFileNameWithoutExtension(_path)}.invalid-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}.json");
            File.Copy(_path, backup, overwrite: false);
            _originalBackedUp = true;
        }

        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("MaaNOP Config 路径没有父目录。 ");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, config, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _path, overwrite: true);
            _protectOriginal = false;
            LoadWarning = null;
        } finally {
            if (File.Exists(tempPath)) {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record LegacyConfig
    {
        public int SchemaVersion { get; init; }
        public IReadOnlyList<string> SelectedTasks { get; init; } = [];
        public IReadOnlyDictionary<string, JsonElement> ExplicitOptions { get; init; } =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private static void Validate(MaaNopConfig config, bool requireActive = true)
    {
        if (config.SchemaVersion != MaaNopConfig.CurrentSchemaVersion || config.Configurations is null) {
            throw new InvalidDataException("MaaNOP Config 必须包含有效的 V2 配置列表。");
        }
        var ids = new HashSet<Guid>();
        foreach (var item in config.Configurations) {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name)) {
                throw new InvalidDataException("配置 Id 必须非空且唯一，名称不能为空。");
            }
            if (item.SelectedTasks is null || item.ExplicitOptions is null
                || item.SelectedTasks.Any(string.IsNullOrWhiteSpace)
                || item.SelectedTasks.Count != item.SelectedTasks.Distinct(StringComparer.Ordinal).Count()) {
                throw new InvalidDataException("SelectedTasks 必须为不重复的 task name 列表。");
            }
            foreach (var (name, value) in item.ExplicitOptions) {
                if (string.IsNullOrWhiteSpace(name) || value.ValueKind != JsonValueKind.Object) {
                    throw new InvalidDataException("ExplicitOptions key 必须非空，value 必须为 object。");
                }
            }
        }
        if (requireActive && !ids.Contains(config.ActiveConfigurationId)) {
            throw new InvalidDataException("ActiveConfigurationId 不存在。");
        }
    }
}
