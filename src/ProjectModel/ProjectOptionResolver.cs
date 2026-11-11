using System.Text.Json;
using System.Text.Json.Nodes;
using Protocol;

namespace ProjectModel;

internal static class ProjectOptionResolver
{
    internal static ResolvedProjectOptions Resolve(
        ProjectDefinition project, TaskDefinition task, TaskConfiguration config, ISet<string>? passwords = null)
    {
        var pipelineOverrides = new JsonArray { ParseObject(task.PipelineOverride) };

        var globalValues = new JsonObject();
        var taskValues = new JsonObject();
        ResolveScope(project, project.GlobalOptions, config, globalValues, pipelineOverrides, "global_option");
        ResolveScope(project, task.Options, config, taskValues, pipelineOverrides, $"task.{task.Name}.option");
        return new ResolvedProjectOptions(ToElement(MaskPasswords(project, globalValues, passwords)),
            ToElement(MaskPasswords(project, taskValues, passwords)), ToElement(pipelineOverrides));
    }

    // Pretask options only become program arguments: without pipeline overrides, every input stays a string.
    // The argument carries passwords as entered; the resolved values are a record that reaches logs, so masked.
    internal static (JsonElement Values, JsonElement Argument) ResolvePretask(
        ProjectDefinition project, PretaskDefinition pretask, TaskConfiguration config, ISet<string>? passwords = null)
    {
        var values = new JsonObject();
        ResolveScope(project, pretask.Options, config, values, null, $"pretask.{pretask.Name}.option");
        var argument = ToElement(values);
        return (ToElement(MaskPasswords(project, values, passwords)), argument);
    }

    // Resolved values are keyed by option name, so every filled password input can be found by its definition.
    // Masking also collects each original value, which telemetry must redact from whatever it uploads.
    private static JsonObject MaskPasswords(ProjectDefinition project, JsonObject resolvedValues,
        ISet<string>? passwords)
    {
        foreach (var (optionName, node) in resolvedValues.ToArray()) {
            if (node is not JsonObject inputs) {
                continue;
            }
            foreach (var input in project.Options[optionName].Inputs.Where(input => input.Password)) {
                if (inputs[input.Name]?.GetValue<string>() is { Length: > 0 } value) {
                    passwords?.Add(value);
                    inputs[input.Name] = ProjectInputEditor.PasswordMask;
                }
            }
        }
        return resolvedValues;
    }

    internal static void ValidateScope(
        ProjectDefinition project, IReadOnlyList<string> optionNames, TaskConfiguration config, string scope)
    {
        ResolveScope(project, optionNames, config, new JsonObject(), new JsonArray(), scope);
    }

    private static void ResolveScope(
        ProjectDefinition project, IReadOnlyList<string> optionNames, TaskConfiguration config,
        JsonObject resolvedValues, JsonArray? pipelineOverrides, string scope)
    {
        foreach (var optionName in optionNames) {
            ResolveOption(project, optionName, config, resolvedValues, pipelineOverrides, scope);
        }
    }

    private static void ResolveOption(
        ProjectDefinition project, string optionName, TaskConfiguration config,
        JsonObject resolvedValues, JsonArray? pipelineOverrides, string scope)
    {
        var option = project.Options[optionName];
        try {
            switch (option.Kind) {
                case OptionDefinitionKind.Input: {
                        var values = new JsonObject();
                        var substitutions = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                        var explicitInputs = ExplicitOptionIntent.ReadInputs(option, config);
                        foreach (var input in option.Inputs) {
                            var resolvedValue = explicitInputs.TryGetValue(input.Name, out var explicitValue)
                                ? explicitValue
                                : input.Default;
                            values[input.Name] = resolvedValue;
                            substitutions[input.Name] = ProjectInputValue.Parse(input, resolvedValue,
                                $"option {optionName} input {input.Name} 的值", typed: pipelineOverrides is not null,
                                isDefault: !explicitInputs.ContainsKey(input.Name));
                        }
                        resolvedValues[optionName] = values;
                        pipelineOverrides?.Add(CreateTemplatedOverride(option.PipelineOverride, substitutions));
                        break;
                    }
                case OptionDefinitionKind.Select:
                case OptionDefinitionKind.Switch:
                case OptionDefinitionKind.Checkbox: {
                        // Checked checkbox cases merge in declaration order, not in the order they were checked.
                        var active = ExplicitOptionIntent.ReadActiveCases(option, config);
                        resolvedValues[optionName] = option.Kind == OptionDefinitionKind.Checkbox
                            ? new JsonArray(active.Select(item => (JsonNode)item.Name).ToArray())
                            : (JsonNode)active[0].Name;
                        foreach (var selected in active) {
                            pipelineOverrides?.Add(ParseObject(selected.PipelineOverride));
                            foreach (var nested in selected.Options) {
                                ResolveOption(project, nested, config, resolvedValues, pipelineOverrides, scope);
                            }
                        }
                        break;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Loader 产生了不支持的 option kind：{option.Kind}。 ");
            }
        } catch (Exception exception) when (exception is InvalidDataException or JsonException) {
            throw new InvalidDataException($"{scope} 解析 {optionName} 失败：{exception.Message}", exception);
        }
    }

    private static JsonObject CreateTemplatedOverride(
        JsonElement template, IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        var node = ParseObject(template);
        Substitute(node, substitutions);
        return node;
    }

    private static void Substitute(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (node is JsonObject obj) {
            foreach (var property in obj.ToArray()) {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text)) {
                    obj[property.Key] = SubstituteString(text, substitutions);
                } else {
                    Substitute(property.Value, substitutions);
                }
            }
        } else if (node is JsonArray array) {
            for (var index = 0; index < array.Count; index++) {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) {
                    array[index] = SubstituteString(text, substitutions);
                } else {
                    Substitute(array[index], substitutions);
                }
            }
        }
    }

    private static JsonNode? SubstituteString(string text, IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (text.Length > 2 && text[0] == '{' && text[^1] == '}') {
            var name = text[1..^1];
            if (substitutions.TryGetValue(name, out var exact)) {
                return exact?.DeepClone();
            }
        }

        var result = text;
        foreach (var (name, value) in substitutions) {
            var replacement = value switch {
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
                null => string.Empty,
                _ => value.ToJsonString(ProtocolJson.Options)
            };
            result = result.Replace($"{{{name}}}", replacement, StringComparison.Ordinal);
        }
        return JsonValue.Create(result);
    }

    private static JsonObject ParseObject(JsonElement element) =>
        JsonNode.Parse(element.GetRawText())!.AsObject();

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(ProtocolJson.Options));
        return document.RootElement.Clone();
    }
}
