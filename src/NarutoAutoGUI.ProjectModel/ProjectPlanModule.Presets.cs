using System.Text.Json;

namespace NarutoAutoGUI.ProjectModel;

public sealed record ProjectPresetChoice(string Name, string Label, string Description);

public sealed partial class ProjectPlanModule
{
    public IReadOnlyList<ProjectPresetChoice> Presets => _project.Presets
        .Select(preset => new ProjectPresetChoice(preset.Name, preset.Label, preset.Description)).ToArray();

    public Guid CreateConfigurationFromPreset(string presetName)
    {
        var preset = _project.Presets.SingleOrDefault(item => item.Name == presetName)
            ?? throw new ArgumentException($"预设不存在：{presetName}。", nameof(presetName));
        // A preset starts from project defaults; nothing is taken from the current configuration. A configuration
        // cannot persist an unchecked item, so a disabled item is left out together with its options.
        var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in preset.Tasks) {
            var task = _project.Tasks.SingleOrDefault(task => task.Name == item.Name)
                ?? throw new InvalidDataException($"预设引用了不存在的任务 {item.Name}。");
            if (item.Enabled) {
                ReadPresetOptions(item, _project.GlobalOptions.Concat(task.Options), options, typed: true);
            }
        }
        var pretasks = new List<string>();
        foreach (var item in preset.Pretasks) {
            var pretask = _project.Pretasks.SingleOrDefault(pretask => pretask.Name == item.Name)
                ?? throw new InvalidDataException($"预设引用了不存在的前置任务 {item.Name}。");
            if (item.Enabled && pretask.Applicable) {
                ReadPresetOptions(item, pretask.Options, options, typed: false);
                pretasks.Add(item.Name);
            }
        }
        var configuration = new TaskConfiguration {
            Id = Guid.NewGuid(), Name = preset.Label,
            SelectedTasks = preset.Tasks.Where(item => item.Enabled).Select(item => item.Name).ToArray(),
            SelectedPretasks = pretasks, ExplicitOptions = options
        };
        ValidateActiveConfiguration(configuration);
        SaveConfig(_config with {
            ActiveConfigurationId = configuration.Id,
            Configurations = _config.Configurations.Append(configuration).ToArray()
        });
        return configuration.Id;
    }

    private void ReadPresetOptions(PresetItem item, IEnumerable<string> roots,
        Dictionary<string, JsonElement> result, bool typed)
    {
        var allowed = ProjectInterfaceLoader.CollectOptionNames(roots, _project.Options);
        foreach (var property in item.Options.EnumerateObject()) {
            if (!allowed.Contains(property.Name)) {
                throw new InvalidDataException($"预设任务 {item.Name} 引用了不属于该任务的选项 {property.Name}。");
            }
            var definition = _project.Options[property.Name];
            JsonElement intent;
            if (definition.Kind == OptionDefinitionKind.Input) {
                if (property.Value.ValueKind != JsonValueKind.Object) {
                    throw new InvalidDataException($"预设 input {property.Name} 必须是字段到 string 的 object。");
                }
                var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var input in property.Value.EnumerateObject()) {
                    var field = definition.Inputs.SingleOrDefault(value => value.Name == input.Name);
                    if (field is null || input.Value.ValueKind != JsonValueKind.String) {
                        throw new InvalidDataException($"预设 input {property.Name}.{input.Name} 未定义或不是 string。");
                    }
                    var value = input.Value.GetString()!;
                    _ = ProjectInputValue.Parse(field, value, $"预设 {property.Name}.{input.Name}", typed);
                    if (!inputs.TryAdd(input.Name, value)) {
                        throw new InvalidDataException($"预设 input {property.Name}.{input.Name} 重复。");
                    }
                }
                if (inputs.Count == 0) {
                    continue;
                }
                intent = ExplicitOptionIntent.CreateInputs(inputs);
            } else {
                if (property.Value.ValueKind != JsonValueKind.String
                    || !definition.Cases.Any(value => value.Name == property.Value.GetString())) {
                    throw new InvalidDataException($"预设选项 {property.Name} 必须是已定义的 case.name 字符串。");
                }
                intent = ExplicitOptionIntent.CreateSelectedCase(property.Value.GetString()!);
            }
            MergePresetOption(result, property.Name, intent);
        }
    }

    // Configurations share option intent by name. Never silently change a different task's preset value.
    private static void MergePresetOption(Dictionary<string, JsonElement> options, string name, JsonElement value)
    {
        if (!options.TryGetValue(name, out var previous)) {
            options.Add(name, value);
        } else if (ExplicitOptionIntent.ReadInputs(previous) is { } merged
            && ExplicitOptionIntent.ReadInputs(value) is { } inputs) {
            foreach (var (field, input) in inputs) {
                if (merged.TryGetValue(field, out var existing) && existing != input) {
                    throw new InvalidDataException($"预设对共享选项 {name}.{field} 指定了不同值，无法创建配置。");
                }
                merged[field] = input;
            }
            options[name] = ExplicitOptionIntent.CreateInputs(merged);
        } else if (!JsonElement.DeepEquals(previous, value)) {
            throw new InvalidDataException($"预设对共享选项 {name} 指定了不同值，无法创建配置。");
        }
    }
}
