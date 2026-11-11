using System.Text.Json;
using System.Text.RegularExpressions;

namespace Worker;

// The focus template a callback carries for its own message: the run log text, if any, and an explicit trace value.
internal readonly record struct MaaFocus(string? LogText, bool? Trace);

internal static class MaaRunLogFormatter
{
    private static readonly Regex PlaceholderPattern = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // PI v2: a string template is the content; an object carries content, display and trace. The run log is the only
    // channel this frontend offers, so content goes there whatever display names.
    internal static MaaFocus Read(string message, string detailsJson)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrWhiteSpace(detailsJson)) {
            return default;
        }

        using var document = JsonDocument.Parse(detailsJson);
        var details = document.RootElement;
        if (details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("focus", out var focus)
            || focus.ValueKind != JsonValueKind.Object
            || !focus.TryGetProperty(message, out var template)) {
            return default;
        }

        JsonElement content;
        bool? trace = null;
        if (template.ValueKind == JsonValueKind.Object) {
            if (template.TryGetProperty("trace", out var traceElement)
                && traceElement.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                trace = traceElement.GetBoolean();
            }
            if (!template.TryGetProperty("content", out content)) {
                return new MaaFocus(null, trace);
            }
        } else {
            content = template;
        }

        var text = content.ValueKind == JsonValueKind.String ? Render(details, content.GetString()) : null;
        return new MaaFocus(text, trace);
    }

    private static string? Render(JsonElement details, string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) {
            return null;
        }

        var rendered = PlaceholderPattern.Replace(
            template,
            match => GetReplacement(details, match.Groups["name"].Value) ?? match.Value);
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    private static string? GetReplacement(JsonElement details, string name)
    {
        if (!details.TryGetProperty(name, out var value)) {
            return null;
        }

        return value.ValueKind switch {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }
}
