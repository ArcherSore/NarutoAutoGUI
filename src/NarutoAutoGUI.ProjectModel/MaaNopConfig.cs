using System.Text.Json;
using System.Text.Json.Serialization;

namespace NarutoAutoGUI.ProjectModel;

public sealed record MaaNopConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<string> SelectedTasks { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> ExplicitOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

internal sealed class MaaNopConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;

    internal MaaNopConfigStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    internal MaaNopConfig Load()
    {
        if (!File.Exists(_path))
        {
            return new MaaNopConfig();
        }

        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        var config = JsonSerializer.Deserialize<MaaNopConfig>(stream, JsonOptions)
                     ?? throw new InvalidDataException("maanop-config.json 为空。 ");
        if (config.SchemaVersion != MaaNopConfig.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持 MaaNOP Config SchemaVersion {config.SchemaVersion}。 ");
        }

        return config;
    }

    internal void Save(MaaNopConfig config)
    {
        if (config.SchemaVersion != MaaNopConfig.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"只能保存 SchemaVersion {MaaNopConfig.CurrentSchemaVersion} MaaNOP Config。 ");
        }

        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("MaaNOP Config 路径没有父目录。 ");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, config, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
