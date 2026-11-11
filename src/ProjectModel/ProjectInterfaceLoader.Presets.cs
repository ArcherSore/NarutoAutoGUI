using System.Text.Json;

namespace ProjectModel;

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

    // interface.json ships with the resource, so a preset must not carry a password.
    private static void RejectPresetPasswords(
        IReadOnlyList<PresetDefinition> presets, IReadOnlyDictionary<string, OptionDefinition> options)
    {
        foreach (var preset in presets) {
            var values = preset.Tasks.Concat(preset.Pretasks).SelectMany(item => item.Options.EnumerateObject());
            foreach (var option in values.Where(option => option.Value.ValueKind == JsonValueKind.Object)) {
                var definition = options.GetValueOrDefault(option.Name);
                foreach (var field in option.Value.EnumerateObject()) {
                    if (definition?.IsPassword(field.Name) == true) {
                        throw new InvalidDataException($"预设 {preset.Name} 不能填写密码字段 {option.Name}.{field.Name}。");
                    }
                }
            }
        }
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
            result.Add(new PresetItem(name, OptionalBoolean(obj, "enabled", itemPath, fallback: true),
                ReadObjectOrEmpty(obj, "option", itemPath)));
        }
        return result;
    }
}
