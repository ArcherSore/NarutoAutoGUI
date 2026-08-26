using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

internal enum OptionDefinitionKind
{
    Select,
    Switch,
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
    IReadOnlyDictionary<string, OptionDefinition> Options);

internal sealed record TaskDefinition(
    string Name, string Label, string Entry,
    IReadOnlyList<string> Options, JsonElement PipelineOverride);

internal sealed record OptionDefinition(
    string Name, string Label, string Description,
    OptionDefinitionKind Kind, string? DefaultCase,
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
