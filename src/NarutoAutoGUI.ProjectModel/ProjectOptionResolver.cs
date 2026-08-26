using System.Text.Json;
using System.Text.Json.Nodes;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

internal static class ProjectOptionResolver
{
    internal static ResolvedProjectOptions Resolve(ProjectDefinition project, TaskDefinition task, MaaNopConfig config)
    {
        var pipelineOverrides = new JsonArray { ParseObject(task.PipelineOverride) };

        var globalValues = new JsonObject();
        var taskValues = new JsonObject();
        ResolveScope(project, project.GlobalOptions, config, globalValues, pipelineOverrides, "global_option");
        ResolveScope(project, task.Options, config, taskValues, pipelineOverrides, $"task.{task.Name}.option");
        return new ResolvedProjectOptions(ToElement(globalValues), ToElement(taskValues), ToElement(pipelineOverrides));
    }

    internal static void ValidateScope(ProjectDefinition project, IReadOnlyList<string> optionNames, MaaNopConfig config, string scope)
    {
        ResolveScope(project, optionNames, config, new JsonObject(), new JsonArray(), scope);
    }

    private static void ResolveScope(
        ProjectDefinition project, IReadOnlyList<string> optionNames, MaaNopConfig config,
        JsonObject resolvedValues, JsonArray pipelineOverrides, string scope)
    {
        foreach (var optionName in optionNames) {
            ResolveOption(project, optionName, config, resolvedValues, pipelineOverrides, scope);
        }
    }

    private static void ResolveOption(
        ProjectDefinition project, string optionName, MaaNopConfig config,
        JsonObject resolvedValues, JsonArray pipelineOverrides, string scope)
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
                            substitutions[input.Name] = ProjectInputValue.Parse(
                                input, resolvedValue, $"option {optionName} input {input.Name} 的值");
                        }
                        resolvedValues[optionName] = values;
                        pipelineOverrides.Add(CreateTemplatedOverride(option.PipelineOverride, substitutions));
                        break;
                    }
                case OptionDefinitionKind.Select:
                case OptionDefinitionKind.Switch: {
                        var selectedName = ExplicitOptionIntent.ReadSelectedCase(option, config)
                                           ?? option.DefaultCase!;
                        var selected = option.Cases.SingleOrDefault(item => item.Name == selectedName)
                                       ?? throw new InvalidDataException(
                                           $"option {optionName} 的 case {selectedName} 不存在。 ");
                        resolvedValues[optionName] = selected.Name;
                        pipelineOverrides.Add(ParseObject(selected.PipelineOverride));
                        foreach (var nested in selected.Options) {
                            ResolveOption(project, nested, config, resolvedValues, pipelineOverrides, scope);
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

    private static JsonObject CreateTemplatedOverride(JsonElement template, IReadOnlyDictionary<string, JsonNode?> substitutions)
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
