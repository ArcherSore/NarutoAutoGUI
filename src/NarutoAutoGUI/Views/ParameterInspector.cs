using NarutoAutoGUI.ProjectModel;

namespace NarutoAutoGUI.Views;

// An invalid, unsaved text for one input; the saved configuration still holds the previous value.
internal sealed record InputDraft(string Text, string Message);

// A lone input is named and described by its row; one of several also names its own label.
internal sealed record InspectorInput(
    ProjectInputEditor Input, string Label, string AccessibleName, string HelpText, InputDraft? Draft,
    string? DefaultHint)
{
    internal string SavedValue => Input.Value;
    internal string DisplayValue => Draft?.Text ?? Input.Value;
}

// One inspector row per option: a select or switch, or an input option with all of its inputs side by side.
internal sealed record InspectorRow(
    ProjectOptionEditor Option, string Label, int Depth, bool IsGlobal, string Description,
    string? CaseDescription, string? DefaultHint, IReadOnlyList<string> SharedWith,
    IReadOnlyList<InspectorInput> Inputs)
{
    internal ProjectOptionKind Kind => Option.Kind;
    internal bool IsInvalid => Inputs.Any(input => input.Draft is not null);
    internal ProjectCaseEditor? SelectedCase => Option.Cases.SingleOrDefault(item => item.Name == Option.SelectedCase);
}

internal static class ParameterInspector
{
    // Global options first, then task options, each in declared order; a selected case's sub-options follow their
    // parent one level deeper. Drafts are keyed by (option, input) of the configuration being shown.
    internal static IReadOnlyList<InspectorRow> Derive(
        ProjectConfigurationView configuration, IReadOnlyDictionary<(string Option, string Input), InputDraft> drafts,
        IReadOnlyList<(string Label, ProjectConfigurationView Configuration)> otherTasks)
    {
        var otherOptions = otherTasks
            .Select(task => (task.Label, Names: Flatten(task.Configuration.TaskOptions, 0)
                .Select(item => item.Option.Name).ToHashSet(StringComparer.Ordinal)))
            .ToArray();
        return Flatten(configuration.GlobalOptions, 0).Select(item => (item.Option, item.Depth, Global: true))
            .Concat(Flatten(configuration.TaskOptions, 0).Select(item => (item.Option, item.Depth, Global: false)))
            .Select(item => CreateRow(item.Option, item.Depth, item.Global, drafts, item.Global ? []
                : otherOptions.Where(other => other.Names.Contains(item.Option.Name))
                    .Select(other => other.Label).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    // An empty value is spelled out so a hint never ends in nothing.
    internal static string ShowValue(string value) => value.Length == 0 ? "空" : value;

    private static string FormatDefault(string value) => $"默认：{ShowValue(value)}";

    private static IEnumerable<(ProjectOptionEditor Option, int Depth)> Flatten(
        IEnumerable<ProjectOptionEditor> options, int depth)
    {
        foreach (var option in options) {
            yield return (option, depth);
            foreach (var child in Flatten(option.ActiveChildren, depth + 1)) {
                yield return child;
            }
        }
    }

    private static InspectorRow CreateRow(ProjectOptionEditor option, int depth, bool global,
        IReadOnlyDictionary<(string Option, string Input), InputDraft> drafts, IReadOnlyList<string> sharedWith)
    {
        // A PI may declare either label as "": the option label, then the first input's, then the option name.
        var label = FirstNonBlank(option.Label, option.Inputs.FirstOrDefault()?.Label) ?? option.Name;
        var single = option.Inputs.Count == 1;
        var description = FirstNonBlank(option.Description, single ? option.Inputs[0].Description : null)
            ?? string.Empty;
        // The default is pointed out only when an explicit value differs from it.
        if (option.Kind != ProjectOptionKind.Input) {
            var selected = option.Cases.SingleOrDefault(item => item.Name == option.SelectedCase);
            var defaultCase = option.Cases.SingleOrDefault(item => item.Name == option.DefaultCase);
            var differs = option.IsExplicit && option.SelectedCase != option.DefaultCase && defaultCase is not null;
            var defaultHint = differs ? FormatDefault(defaultCase!.Label) : null;
            return new InspectorRow(option, label, depth, global, description, FirstNonBlank(selected?.Description),
                defaultHint, sharedWith, []);
        }
        var inputs = option.Inputs.Select(input => {
            var draft = drafts.GetValueOrDefault((option.Name, input.Name));
            var differs = draft is null && input.IsExplicit && input.Value != input.DefaultValue;
            // An input without a label keeps the old fallback: the option label, then the option name.
            var inputLabel = FirstNonBlank(input.Label, option.Label) ?? option.Name;
            var name = single || inputLabel == label ? label : $"{label} · {inputLabel}";
            return new InspectorInput(input, inputLabel, name, single ? description : input.Description, draft,
                differs ? FormatDefault(input.DefaultValue) : null);
        }).ToArray();
        return new InspectorRow(option, label, depth, global, description, null, null, sharedWith, inputs);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
