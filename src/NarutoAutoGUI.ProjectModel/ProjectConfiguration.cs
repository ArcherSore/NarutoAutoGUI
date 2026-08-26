using System.Text.Json;
using System.Text.Json.Nodes;

namespace NarutoAutoGUI.ProjectModel;

public enum ProjectOptionKind { Input, Select, Switch }

public sealed record ProjectInputEditor(
    string Name, string Label, string Description, string DefaultValue, string Value,
    bool IsExplicit, string? Verify, string? PatternMessage);

public sealed record ProjectCaseEditor(string Name, string Label, string Description);

public sealed record ProjectOptionEditor(
    string Name, string Label, string Description, ProjectOptionKind Kind, bool IsExplicit,
    string? SelectedCase, string? DefaultCase,
    IReadOnlyList<ProjectCaseEditor> Cases, IReadOnlyList<ProjectInputEditor> Inputs,
    IReadOnlyList<ProjectOptionEditor> ActiveChildren);

public sealed record ProjectConfigurationView(
    IReadOnlyList<ProjectOptionEditor> GlobalOptions, IReadOnlyList<ProjectOptionEditor> TaskOptions);

internal static class ExplicitOptionIntent
{
    internal static IReadOnlyDictionary<string, string> ReadInputs(OptionDefinition option, MaaNopConfig config)
    {
        if (!config.ExplicitOptions.TryGetValue(option.Name, out var element)) {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        var root = RequireObject(element, option.Name);
        RejectUnknown(root, "Inputs", option.Name);
        if (!root.TryGetProperty("Inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException(
                $"ExplicitOptions.{option.Name}.Inputs 必须是 object。 ");
        }

        var allowed = option.Inputs.Select(input => input.Name)
            .ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in inputs.EnumerateObject()) {
            if (!allowed.Contains(property.Name)) {
                throw new InvalidDataException(
                    $"ExplicitOptions.{option.Name}.Inputs 包含未知字段 {property.Name}。 ");
            }
            if (property.Value.ValueKind != JsonValueKind.String) {
                throw new InvalidDataException(
                    $"ExplicitOptions.{option.Name}.Inputs.{property.Name} 必须是 string。 ");
            }
            result.Add(property.Name, property.Value.GetString()!);
        }
        if (result.Count == 0) {
            throw new InvalidDataException(
                $"ExplicitOptions.{option.Name}.Inputs 不能为空；跟随默认时应删除该 option intent。 ");
        }
        return result;
    }

    internal static string? ReadSelectedCase(OptionDefinition option, MaaNopConfig config)
    {
        if (!config.ExplicitOptions.TryGetValue(option.Name, out var element)) {
            return null;
        }
        var root = RequireObject(element, option.Name);
        RejectUnknown(root, "SelectedCase", option.Name);
        if (!root.TryGetProperty("SelectedCase", out var selected)
            || selected.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(selected.GetString())) {
            throw new InvalidDataException(
                $"ExplicitOptions.{option.Name}.SelectedCase 必须是非空 string。 ");
        }
        return selected.GetString();
    }

    internal static JsonElement CreateInputs(IReadOnlyDictionary<string, string> values)
    {
        var inputs = new JsonObject();
        foreach (var (name, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            inputs[name] = value;
        }
        return ToElement(new JsonObject { ["Inputs"] = inputs });
    }

    internal static JsonElement CreateSelectedCase(string selectedCase) =>
        ToElement(new JsonObject { ["SelectedCase"] = selectedCase });

    private static JsonElement RequireObject(JsonElement element, string optionName)
    {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException(
                $"ExplicitOptions.{optionName} 必须是 object。 ");
        }
        return element;
    }

    private static void RejectUnknown(JsonElement element, string allowed, string optionName)
    {
        foreach (var property in element.EnumerateObject()) {
            if (!string.Equals(property.Name, allowed, StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    $"ExplicitOptions.{optionName} 包含未知字段 {property.Name}。 ");
            }
        }
    }

    private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);
}
