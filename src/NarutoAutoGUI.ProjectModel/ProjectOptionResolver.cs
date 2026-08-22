using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

internal static class ProjectOptionResolver
{
    internal static ResolvedProjectOptions Resolve(
        ProjectDefinition project,
        TaskDefinition task,
        MaaNopConfig config)
    {
        var merged = new JsonObject();
        MergeObject(merged, JsonNode.Parse(task.PipelineOverride.GetRawText())!.AsObject());

        var globalValues = new JsonObject();
        var taskValues = new JsonObject();
        ResolveScope(project, project.GlobalOptions, config, globalValues, merged, "global_option");
        ResolveScope(project, project.ResourceOptions, config, new JsonObject(), merged, "resource.option");
        ResolveScope(project, project.ControllerOptions, config, new JsonObject(), merged, "controller.option");
        ResolveScope(project, task.Options, config, taskValues, merged, $"task.{task.Name}.option");
        return new ResolvedProjectOptions(
            ToElement(globalValues),
            ToElement(taskValues),
            ToElement(merged));
    }

    internal static void ValidateScope(
        ProjectDefinition project,
        IReadOnlyList<string> optionNames,
        MaaNopConfig config,
        string scope)
    {
        ResolveScope(
            project,
            optionNames,
            config,
            new JsonObject(),
            new JsonObject(),
            scope);
    }

    private static void ResolveScope(
        ProjectDefinition project,
        IReadOnlyList<string> optionNames,
        MaaNopConfig config,
        JsonObject resolvedValues,
        JsonObject mergedPipeline,
        string scope)
    {
        foreach (var optionName in optionNames)
        {
            ResolveOption(
                project,
                optionName,
                config,
                resolvedValues,
                mergedPipeline,
                scope);
        }
    }

    private static void ResolveOption(
        ProjectDefinition project,
        string optionName,
        MaaNopConfig config,
        JsonObject resolvedValues,
        JsonObject mergedPipeline,
        string scope)
    {
        var option = project.Options[optionName];
        try
        {
            switch (option.Type)
            {
                case "input":
                {
                    var values = new JsonObject();
                    var substitutions = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                    var explicitInputs = ExplicitOptionIntent.ReadInputs(option, config);
                    foreach (var input in option.Inputs)
                    {
                        var resolvedValue = explicitInputs.TryGetValue(input.Name, out var explicitValue)
                            ? explicitValue
                            : input.Default;
                        if (input.Verify is not null)
                        {
                            var regex = new Regex(
                                input.Verify,
                                RegexOptions.CultureInvariant,
                                TimeSpan.FromSeconds(1));
                            if (!regex.IsMatch(resolvedValue))
                            {
                                throw new InvalidDataException(
                                    input.PatternMessage is null
                                        ? $"option {optionName} input {input.Name} 的值未通过 verify。 "
                                        : $"option {optionName} input {input.Name}: {input.PatternMessage}");
                            }
                        }
                        values[input.Name] = resolvedValue;
                        substitutions[input.Name] = ConvertPipelineValue(
                            optionName,
                            input,
                            resolvedValue);
                    }
                    resolvedValues[optionName] = values;
                    MergeTemplated(mergedPipeline, option.PipelineOverride, substitutions);
                    break;
                }
                case "select":
                case "switch":
                {
                    var selectedName = ExplicitOptionIntent.ReadSelectedCase(option, config)
                                       ?? option.DefaultCase!;
                    var selected = option.Cases.SingleOrDefault(item => item.Name == selectedName)
                                   ?? throw new InvalidDataException(
                                       $"option {optionName} 的 case {selectedName} 不存在。 ");
                    resolvedValues[optionName] = selected.Name;
                    MergeObject(
                        mergedPipeline,
                        JsonNode.Parse(option.PipelineOverride.GetRawText())!.AsObject());
                    MergeObject(
                        mergedPipeline,
                        JsonNode.Parse(selected.PipelineOverride.GetRawText())!.AsObject());
                    foreach (var nested in selected.Options)
                    {
                        ResolveOption(
                            project,
                            nested,
                            config,
                            resolvedValues,
                            mergedPipeline,
                            scope);
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Loader 产生了不支持的 option type：{option.Type}。 ");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new InvalidDataException($"{scope} 解析 {optionName} 失败：{exception.Message}", exception);
        }
    }

    private static JsonNode? ConvertPipelineValue(
        string optionName,
        InputDefinition input,
        string value)
    {
        return input.PipelineType switch
        {
            "string" => JsonValue.Create(value),
            "int" when int.TryParse(value, out var intValue) => JsonValue.Create(intValue),
            "bool" when bool.TryParse(value, out var boolValue) => JsonValue.Create(boolValue),
            "int" => throw new InvalidDataException(
                $"option {optionName} input {input.Name} 的值不是合法 int。 "),
            "bool" => throw new InvalidDataException(
                $"option {optionName} input {input.Name} 的值不是合法 bool。 "),
            _ => throw new InvalidOperationException(
                $"Loader 产生了不支持的 pipeline_type：{input.PipelineType}。 ")
        };
    }

    private static void MergeTemplated(
        JsonObject target,
        JsonElement template,
        IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        var node = JsonNode.Parse(template.GetRawText())!.AsObject();
        Substitute(node, substitutions);
        MergeObject(target, node);
    }

    private static void Substitute(
        JsonNode? node,
        IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    obj[property.Key] = SubstituteString(text, substitutions);
                }
                else
                {
                    Substitute(property.Value, substitutions);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    array[index] = SubstituteString(text, substitutions);
                }
                else
                {
                    Substitute(array[index], substitutions);
                }
            }
        }
    }

    private static JsonNode? SubstituteString(
        string text,
        IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (text.Length > 2 && text[0] == '{' && text[^1] == '}')
        {
            var name = text[1..^1];
            if (substitutions.TryGetValue(name, out var exact))
            {
                return exact?.DeepClone();
            }
        }

        var result = text;
        foreach (var (name, value) in substitutions)
        {
            var replacement = value switch
            {
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
                null => string.Empty,
                _ => value.ToJsonString(ProtocolJson.Options)
            };
            result = result.Replace($"{{{name}}}", replacement, StringComparison.Ordinal);
        }
        return JsonValue.Create(result);
    }

    private static void MergeObject(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonObject sourceObject
                && target[property.Key] is JsonObject targetObject)
            {
                MergeObject(targetObject, sourceObject);
            }
            else
            {
                target[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(ProtocolJson.Options));
        return document.RootElement.Clone();
    }
}
