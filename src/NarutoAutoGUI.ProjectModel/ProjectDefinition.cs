using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

internal enum OptionDefinitionKind
{
    Select,
    Switch,
    Checkbox,
    Input
}

internal enum PipelineValueKind
{
    String,
    Int,
    Bool
}

internal sealed record ProjectDefinition(
    string ProjectRoot, ProjectProvenance Provenance, Win32ControllerDefinition Controller,
    IReadOnlyList<ResourceDefinition> Resources, AgentDefinition Agent, string RuntimeProfileDigest,
    IReadOnlyList<string> GlobalOptions, IReadOnlyList<TaskDefinition> Tasks,
    IReadOnlyDictionary<string, OptionDefinition> Options, IReadOnlyList<ProjectTaskGroup> Groups,
    IReadOnlyList<PretaskDefinition> Pretasks, IReadOnlyList<PresetDefinition> Presets, string? Label,
    IReadOnlyList<string> Welcome, ProjectAbout About);

internal sealed record PresetDefinition(
    string Name, string Label, string Description, IReadOnlyList<PresetItem> Tasks,
    IReadOnlyList<PresetItem> Pretasks);

internal sealed record PresetItem(string Name, bool Enabled, JsonElement Options);

internal sealed record PretaskDefinition(
    string Name, string Label, string Description, string Executable, IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Options, bool Applicable);

internal sealed record TaskDefinition(
    string Name, string Label, string Description, string Entry,
    IReadOnlyList<string> Options, JsonElement PipelineOverride, IReadOnlyList<string> Groups);

internal sealed record OptionDefinition(
    string Name, string Label, string Description,
    OptionDefinitionKind Kind, string? DefaultCase, IReadOnlyList<string> DefaultCases, int MinCount, int? MaxCount,
    IReadOnlyList<InputDefinition> Inputs, IReadOnlyList<CaseDefinition> Cases,
    JsonElement PipelineOverride);

internal sealed record InputDefinition(
    string Name, string Label, string Description, string Default,
    PipelineValueKind PipelineKind, string? Verify, string? PatternMessage);

internal sealed record CaseDefinition(
    string Name, string Label, string Description,
    IReadOnlyList<string> Options, JsonElement PipelineOverride);

internal sealed record ResolvedProjectOptions(
    JsonElement ResolvedGlobalOptions, JsonElement ResolvedTaskOptions,
    JsonElement PipelineOverride);
