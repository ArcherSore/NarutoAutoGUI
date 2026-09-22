using System.Text.Json;
using System.Text.Json.Serialization;

namespace NarutoAutoGUI.Settings;

internal enum SettingsItemKind
{
    Toggle, Action, Info
}

internal sealed record SettingsDefinition
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required SettingsSectionDefinition[] Sections { get; init; }

    internal static SettingsDefinition Load()
    {
        using var stream = typeof(SettingsDefinition).Assembly
            .GetManifestResourceStream("NarutoAutoGUI.Assets.settings.json")
            ?? throw new InvalidDataException("缺少内置 Settings Definition：Assets/settings.json。");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal static SettingsDefinition Parse(string json)
    {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter<SettingsItemKind>(JsonNamingPolicy.CamelCase, false));
        var definition = JsonSerializer.Deserialize<SettingsDefinition>(json, options)
            ?? throw new InvalidDataException("Settings Definition 不能为空。");
        Require(definition.Title, "title");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in definition.Sections
            ?? throw new InvalidDataException("Settings Definition 缺少 sections。")) {
            if (section is null) { throw new InvalidDataException("Settings section 不能为空。"); }
            RequireId(section.Id);
            Require(section.Title, $"{section.Id}.title");
            foreach (var item in section.Items
                ?? throw new InvalidDataException($"Settings section {section.Id} 缺少 items。")) {
                if (item is null) { throw new InvalidDataException($"Settings section {section.Id} 中的 item 不能为空。"); }
                RequireId(item.Id);
                switch (item.Type) {
                    case SettingsItemKind.Toggle:
                        Require(item.Title, $"{item.Id}.title");
                        Require(item.SettingKey, $"{item.Id}.settingKey");
                        break;
                    case SettingsItemKind.Action:
                        Require(item.Title, $"{item.Id}.title");
                        Require(item.ButtonText, $"{item.Id}.buttonText");
                        Require(item.ActionId, $"{item.Id}.actionId");
                        break;
                    case SettingsItemKind.Info:
                        if (string.IsNullOrWhiteSpace(item.Title) && string.IsNullOrWhiteSpace(item.Description)
                            && string.IsNullOrWhiteSpace(item.ValueKey)) {
                            throw new InvalidDataException($"Settings info {item.Id} 缺少展示内容。");
                        }
                        break;
                }
                if (item.Type != SettingsItemKind.Toggle && item.SettingKey is not null
                    || item.Type != SettingsItemKind.Action
                        && (item.ActionId is not null || item.ButtonText is not null)
                    || item.Type != SettingsItemKind.Info && item.ValueKey is not null) {
                    throw new InvalidDataException($"Settings item {item.Id} 包含不属于 {item.Type} 的绑定字段。");
                }
            }
        }
        return definition;

        void RequireId(string id)
        {
            Require(id, "id");
            if (!ids.Add(id)) {
                throw new InvalidDataException($"Settings Definition 存在重复 id：{id}。");
            }
        }
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidDataException($"Settings Definition 缺少 {field}。");
        }
    }
}

internal sealed record SettingsSectionDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required SettingsItemDefinition[] Items { get; init; }
}

internal sealed record SettingsItemDefinition
{
    public required string Id { get; init; }
    public required SettingsItemKind Type { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? SettingKey { get; init; }
    public string? ActionId { get; init; }
    public string? ButtonText { get; init; }
    public string? ValueKey { get; init; }
}
