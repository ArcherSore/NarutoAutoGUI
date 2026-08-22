using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NarutoAutoGUI.ProjectModel;

internal static class ProjectInputValue
{
    internal static JsonNode Parse(
        InputDefinition input,
        string value,
        string context)
    {
        ValidatePattern(input, value, context);
        return input.PipelineKind switch
        {
            PipelineValueKind.String => JsonValue.Create(value)!,
            PipelineValueKind.Int when int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var intValue) => JsonValue.Create(intValue)!,
            PipelineValueKind.Bool when bool.TryParse(value, out var boolValue) =>
                JsonValue.Create(boolValue)!,
            PipelineValueKind.Int => throw new InvalidDataException(
                $"{context} 不是合法 int。 "),
            PipelineValueKind.Bool => throw new InvalidDataException(
                $"{context} 不是合法 bool。 "),
            _ => throw new InvalidOperationException(
                $"Loader 产生了不支持的 pipeline kind：{input.PipelineKind}。 ")
        };
    }

    private static void ValidatePattern(
        InputDefinition input,
        string value,
        string context)
    {
        if (input.Verify is null)
        {
            return;
        }

        const string defaultSuffix = ".default";
        var verifyContext = context.EndsWith(defaultSuffix, StringComparison.Ordinal)
            ? $"{context[..^defaultSuffix.Length]}.verify"
            : $"{context}的 verify";
        Regex regex;
        try
        {
            regex = new Regex(
                input.Verify,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"{verifyContext} 不是合法正则表达式。",
                exception);
        }

        try
        {
            if (!regex.IsMatch(value))
            {
                throw new InvalidDataException(
                    input.PatternMessage is null
                        ? $"{context} 未通过 verify。 "
                        : $"{context}: {input.PatternMessage}");
            }
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new InvalidDataException(
                $"{verifyContext} 执行超时。",
                exception);
        }
    }
}
