using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NarutoAutoGUI.ProjectModel;

public enum ProjectOptionKind { Input, Select, Switch, Checkbox }

// The editor an input asks for; the value is a plain string either way. A password is never a file or folder.
public enum ProjectInputType { Text, Password, File, Directory }

public sealed record ProjectInputEditor(
    string Name, string Label, string Description, string DefaultValue, string Value,
    bool IsExplicit, string? Verify, string? PatternMessage)
{
    // Shown in place of a filled password, in the GUI and in logs alike.
    public const string PasswordMask = "••••••";

    // Set when the value still fails verify; it may be shown and saved around, but blocks Start.
    public string? Error { get; init; }
    public ProjectInputType InputType { get; init; }
    public bool IsPassword => InputType == ProjectInputType.Password;
}

public sealed record ProjectCaseEditor(string Name, string Label, string Description);

public sealed record ProjectOptionEditor(
    string Name, string Label, string Description, ProjectOptionKind Kind, bool IsExplicit,
    string? SelectedCase, string? DefaultCase,
    IReadOnlyList<ProjectCaseEditor> Cases, IReadOnlyList<ProjectInputEditor> Inputs,
    IReadOnlyList<ProjectOptionEditor> ActiveChildren)
{
    // The active cases in declaration order: the one select/switch case, or every checked checkbox case.
    public IReadOnlyList<string> SelectedCases { get; init; } = [];
    public int MinCount { get; init; }
    public int? MaxCount { get; init; }

    // A checkbox below min_count may be saved, but a Run cannot start with it.
    public bool IsBelowMinimum => SelectedCases.Count < MinCount;

    // Why a Run cannot start with this option yet, or null when it can.
    public string? StartBlocker => IsBelowMinimum ? $"至少选择 {MinCount} 项"
        : Inputs.Select(input => input.Error).FirstOrDefault(error => error is not null);
}

public sealed record ProjectConfigurationView(
    IReadOnlyList<ProjectOptionEditor> GlobalOptions, IReadOnlyList<ProjectOptionEditor> TaskOptions);

internal static class ExplicitOptionIntent
{
    internal static IReadOnlyDictionary<string, string> ReadInputs(OptionDefinition option, TaskConfiguration config)
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

        var definitions = option.Inputs.ToDictionary(input => input.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in inputs.EnumerateObject()) {
            if (!definitions.TryGetValue(property.Name, out var definition)) {
                throw new InvalidDataException(
                    $"ExplicitOptions.{option.Name}.Inputs 包含未知字段 {property.Name}。 ");
            }
            if (property.Value.ValueKind != JsonValueKind.String) {
                throw new InvalidDataException(
                    $"ExplicitOptions.{option.Name}.Inputs.{property.Name} 必须是 string。 ");
            }
            var value = property.Value.GetString()!;
            if ((definition.Password ? Unprotect(value) : value) is { } read) {
                result.Add(property.Name, read);
            }
        }
        if (!inputs.EnumerateObject().Any()) {
            throw new InvalidDataException(
                $"ExplicitOptions.{option.Name}.Inputs 不能为空；跟随默认时应删除该 option intent。 ");
        }
        return result;
    }

    // The cases a select, switch or checkbox uses, in declaration order; without an explicit choice, its defaults.
    internal static IReadOnlyList<CaseDefinition> ReadActiveCases(OptionDefinition option, TaskConfiguration config)
    {
        IReadOnlyList<string> names = option.Kind == OptionDefinitionKind.Checkbox
            ? ReadSelectedCases(option, config) ?? option.DefaultCases
            : [ReadSelectedCase(option, config) ?? option.DefaultCase!];
        if (names.FirstOrDefault(name => option.Cases.All(item => item.Name != name)) is { } unknown) {
            throw new InvalidDataException($"option {option.Name} 的 case {unknown} 不存在。 ");
        }
        return option.Cases.Where(item => names.Contains(item.Name)).ToArray();
    }

    private static IReadOnlyList<string>? ReadSelectedCases(OptionDefinition option, TaskConfiguration config)
    {
        if (!config.ExplicitOptions.TryGetValue(option.Name, out var element)) {
            return null;
        }
        var root = RequireObject(element, option.Name);
        RejectUnknown(root, "SelectedCases", option.Name);
        if (!root.TryGetProperty("SelectedCases", out var selected) || selected.ValueKind != JsonValueKind.Array
            || selected.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) {
            throw new InvalidDataException(
                $"ExplicitOptions.{option.Name}.SelectedCases 必须是 string 数组。 ");
        }
        return selected.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static string? ReadSelectedCase(OptionDefinition option, TaskConfiguration config)
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

    internal static JsonElement CreateInputs(OptionDefinition option, IReadOnlyDictionary<string, string> values)
    {
        var inputs = new JsonObject();
        foreach (var (name, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            inputs[name] = option.IsPassword(name) ? Protect(value) : value;
        }
        return ToElement(new JsonObject { ["Inputs"] = inputs });
    }

    // Passwords stay DPAPI-encrypted for the current Windows user in the configuration, also in memory, and are
    // decrypted only when read. A value that cannot be decrypted, e.g. a configuration copied from another user, reads
    // as never entered: a required password then blocks Start like an unfilled field, but every input stays editable.
    private static string Protect(string value) => value.Length == 0 ? value : Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string value)
    {
        try {
            return value.Length == 0 ? value : Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        } catch (Exception exception) when (exception is FormatException or CryptographicException) {
            return null;
        }
    }

    internal static JsonElement CreateSelectedCase(string selectedCase) =>
        ToElement(new JsonObject { ["SelectedCase"] = selectedCase });

    internal static JsonElement CreateSelectedCases(IEnumerable<string> selectedCases) => ToElement(new JsonObject {
        ["SelectedCases"] = new JsonArray(selectedCases.Select(name => (JsonNode)name).ToArray())
    });

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
