using System.Text.Json;
using PresetValues = System.Collections.Generic.Dictionary<(string Option, string? Field), string>;

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
        var values = new PresetValues();
        var tasks = new List<string>();
        foreach (var item in preset.Tasks) {
            var task = _project.Tasks.SingleOrDefault(task => task.Name == item.Name)
                ?? throw new InvalidDataException($"预设引用了不存在的任务 {item.Name}。");
            if (item.Enabled) {
                ReadPresetOptions(item, _project.GlobalOptions.Concat(task.Options), values);
                tasks.Add(item.Name);
            }
        }
        var pretasks = new List<string>();
        foreach (var item in preset.Pretasks) {
            var pretask = _project.Pretasks.SingleOrDefault(pretask => pretask.Name == item.Name)
                ?? throw new InvalidDataException($"预设引用了不存在的前置任务 {item.Name}。");
            if (item.Enabled && pretask.Applicable) {
                ReadPresetOptions(item, pretask.Options, values);
                pretasks.Add(item.Name);
            }
        }
        var options = values.GroupBy(pair => pair.Key.Option).ToDictionary(group => group.Key, group =>
            _project.Options[group.Key].Kind == OptionDefinitionKind.Input
                ? ExplicitOptionIntent.CreateInputs(group.ToDictionary(pair => pair.Key.Field!, pair => pair.Value))
                : ExplicitOptionIntent.CreateSelectedCase(group.Single().Value), StringComparer.Ordinal);
        var configuration = new TaskConfiguration {
            Id = Guid.NewGuid(), Name = preset.Label, SelectedTasks = tasks, SelectedPretasks = pretasks,
            ExplicitOptions = options
        };
        // Values are checked here as for any edit, so only their shape is read above.
        ValidateActiveConfiguration(configuration);
        return AddConfiguration(configuration);
    }

    private void ReadPresetOptions(PresetItem item, IEnumerable<string> roots, PresetValues values)
    {
        var allowed = ProjectInterfaceLoader.CollectOptionNames(roots, _project.Options);
        foreach (var property in item.Options.EnumerateObject()) {
            if (!allowed.Contains(property.Name)) {
                throw new InvalidDataException($"预设任务 {item.Name} 引用了不属于该任务的选项 {property.Name}。");
            }
            var definition = _project.Options[property.Name];
            if (definition.Kind != OptionDefinitionKind.Input) {
                if (property.Value.ValueKind != JsonValueKind.String
                    || !definition.Cases.Any(value => value.Name == property.Value.GetString())) {
                    throw new InvalidDataException($"预设选项 {property.Name} 必须是已定义的 case.name 字符串。");
                }
                AddPresetValue(values, (property.Name, null), property.Value.GetString()!);
            } else if (property.Value.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException($"预设 input {property.Name} 必须是字段到 string 的 object。");
            } else {
                foreach (var input in property.Value.EnumerateObject()) {
                    if (!definition.Inputs.Any(value => value.Name == input.Name)
                        || input.Value.ValueKind != JsonValueKind.String) {
                        throw new InvalidDataException($"预设 input {property.Name}.{input.Name} 未定义或不是 string。");
                    }
                    AddPresetValue(values, (property.Name, input.Name), input.Value.GetString()!);
                }
            }
        }
    }

    // Configurations share option values by name. Never silently change a different task's preset value.
    private static void AddPresetValue(PresetValues values, (string Option, string? Field) key, string value)
    {
        if (values.TryGetValue(key, out var existing) && existing != value) {
            var name = key.Field is null ? key.Option : $"{key.Option}.{key.Field}";
            throw new InvalidDataException($"预设对共享选项 {name} 指定了不同值，无法创建配置。");
        }
        values[key] = value;
    }
}
