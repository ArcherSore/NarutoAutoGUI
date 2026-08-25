using System.Text.Json;
using System.Text.RegularExpressions;

namespace NarutoAutoWorker;

internal static class MaaRunLogFormatter
{
    private static readonly Regex PlaceholderPattern = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    internal static string? Format(string message, string detailsJson)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(detailsJson);
        var details = document.RootElement;
        if (details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("focus", out var focus)
            || focus.ValueKind != JsonValueKind.Object
            || !focus.TryGetProperty(message, out var templateElement)
            || templateElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var template = templateElement.GetString();
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var rendered = PlaceholderPattern.Replace(
            template,
            match => GetReplacement(details, match.Groups["name"].Value) ?? match.Value);
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    private static string? GetReplacement(JsonElement details, string name)
    {
        if (!details.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }
}
