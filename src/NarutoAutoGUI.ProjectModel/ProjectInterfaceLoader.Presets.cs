using System.Text.Json;

namespace NarutoAutoGUI.ProjectModel;

internal static partial class ProjectInterfaceLoader
{
    private static readonly HashSet<string> AllowedPresetProperties = new(StringComparer.Ordinal) {
        "name", "label", "description", "icon", "task", "pretask"
    };
    private static readonly HashSet<string> AllowedPresetItemProperties = new(StringComparer.Ordinal) {
        "name", "enabled", "option"
    };

    private static IReadOnlyList<PresetDefinition> ParsePresets(JsonElement root)
    {
        if (!root.TryGetProperty("preset", out var presets)) {
            return [];
        }
        RequireArrayValue(presets, "$.preset");
        var result = new List<PresetDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in presets.EnumerateArray()) {
            var path = $"$.preset[{index++}]";
            var obj = RequireObject(element, path);
            RejectUnknownProperties(obj, AllowedPresetProperties, path);
            var name = RequireString(obj, "name", path);
            if (!names.Add(name)) {
                throw new InvalidDataException($"重复 preset 标识 {name}。");
            }
            var label = ReadDisplayString(obj, "label");
            _ = ReadDisplayString(obj, "icon");
            result.Add(new PresetDefinition(name, string.IsNullOrWhiteSpace(label) ? name : label,
                ReadOptionalDescription(obj, "description") ?? string.Empty,
                obj.TryGetProperty("task", out var tasks) ? ParsePresetItems(tasks, $"{path}.task") : [],
                obj.TryGetProperty("pretask", out var pretasks) ? ParsePresetItems(pretasks, $"{path}.pretask") : []));
        }
        return result;
    }

    private static IReadOnlyList<PresetItem> ParsePresetItems(JsonElement array, string path)
    {
        RequireArrayValue(array, path);
        var result = new List<PresetItem>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray()) {
            var itemPath = $"{path}[{index++}]";
            var obj = RequireObject(element, itemPath);
            RejectUnknownProperties(obj, AllowedPresetItemProperties, itemPath);
            var name = RequireString(obj, "name", itemPath);
            if (!names.Add(name)) {
                throw new InvalidDataException($"{path} 不支持重复任务 {name}。");
            }
            var enabled = true;
            if (obj.TryGetProperty("enabled", out var value)) {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                    throw new InvalidDataException($"{itemPath}.enabled 必须是 boolean。");
                }
                enabled = value.GetBoolean();
            }
            result.Add(new PresetItem(name, enabled, ReadObjectOrEmpty(obj, "option", itemPath)));
        }
        return result;
    }
}
