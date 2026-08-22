using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

public sealed record ProjectTaskChoice(string Name, string Label);

public sealed record RunStartAttempt(Guid RunId, DateTime CreatedAtUtc, RunPlan Plan, string PlanDigest);

public sealed class ProjectPlanModule
{
    private readonly ProjectDefinition _project;
    private readonly MaaNopConfigStore _configStore;

    private ProjectPlanModule(ProjectDefinition project, MaaNopConfigStore configStore)
    {
        _project = project;
        _configStore = configStore;
        Tasks = project.Tasks
            .Select(task => new ProjectTaskChoice(task.Name, task.Label))
            .ToArray();

        var config = configStore.Load();
        ValidateConfigShape(config);
        SelectedTaskName = config.SelectedTasks.SingleOrDefault();
        if (SelectedTaskName is not null && !project.Tasks.Any(task => task.Name == SelectedTaskName))
        {
            throw new InvalidDataException(
                $"MaaNOP Config 选择的 task 不再存在：{SelectedTaskName}。 ");
        }
    }

    public string ProjectDirectory => _project.ProjectRoot;
    public string ProjectName => _project.Provenance.Name;
    public string ProjectVersion => _project.Provenance.Version;
    public string RuntimeProfileDigest => _project.RuntimeProfileDigest;
    public string SourceInterfaceDigest => _project.Provenance.SourceInterfaceDigest;
    public IReadOnlyList<ProjectTaskChoice> Tasks { get; }
    public string? SelectedTaskName { get; private set; }

    public static ProjectPlanModule Open(string projectDirectory, string configPath)
    {
        var project = ProjectInterfaceLoader.Load(projectDirectory);
        return new ProjectPlanModule(project, new MaaNopConfigStore(configPath));
    }

    public void SelectTask(string taskName)
    {
        _ = Tasks.SingleOrDefault(candidate => candidate.Name == taskName)
            ?? throw new ArgumentException($"PI 中不存在 task：{taskName}。", nameof(taskName));

        var current = LoadConfig();
        var updated = current with
        {
            SelectedTasks = [taskName]
        };
        _ = ProjectOptionResolver.Resolve(_project, FindTask(taskName), updated);
        _configStore.Save(updated);
        SelectedTaskName = taskName;
    }

    public ProjectConfigurationView GetConfiguration()
    {
        var config = LoadConfig();
        ValidateActiveConfiguration(config);
        return BuildConfiguration(config);
    }

    public ProjectConfigurationView SetInputValue(string optionName, string inputName, string value)
    {
        var option = FindOption(optionName);
        if (option.Kind != OptionDefinitionKind.Input)
        {
            throw new ArgumentException($"option {optionName} 不是 input。", nameof(optionName));
        }
        if (!option.Inputs.Any(input => input.Name == inputName))
        {
            throw new ArgumentException(
                $"option {optionName} 不包含 input {inputName}。",
                nameof(inputName));
        }

        var config = LoadConfig();
        var values = ExplicitOptionIntent.ReadInputs(option, config)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values[inputName] = value;
        var updated = ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateInputs(values));
        ValidateActiveConfiguration(updated);
        _configStore.Save(updated);
        return BuildConfiguration(updated);
    }

    public ProjectConfigurationView SetSelectedCase(string optionName, string selectedCase)
    {
        var option = FindOption(optionName);
        if (option.Kind is not (OptionDefinitionKind.Select or OptionDefinitionKind.Switch))
        {
            throw new ArgumentException(
                $"option {optionName} 不是 select/switch。",
                nameof(optionName));
        }
        if (!option.Cases.Any(item => item.Name == selectedCase))
        {
            throw new ArgumentException(
                $"option {optionName} 不包含 case {selectedCase}。",
                nameof(selectedCase));
        }

        var config = LoadConfig();
        var updated = ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateSelectedCase(selectedCase));
        ValidateActiveConfiguration(updated);
        _configStore.Save(updated);
        return BuildConfiguration(updated);
    }

    public ProjectConfigurationView FollowProjectDefault(string optionName)
    {
        _ = FindOption(optionName);
        var config = LoadConfig();
        var values = config.ExplicitOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values.Remove(optionName);
        var updated = config with { ExplicitOptions = values };
        ValidateActiveConfiguration(updated);
        _configStore.Save(updated);
        return BuildConfiguration(updated);
    }

    public LaunchManifest CreateLaunchManifest(Guid workerInstanceId) => new(
        ProtocolConstants.LaunchContextVersion,
        workerInstanceId,
        _project.RuntimeProfileDigest,
        _project.ProjectRoot,
        _project.Provenance,
        _project.Controller,
        _project.Resources,
        _project.Agent);

    public RunStartAttempt CreateRunStartAttempt()
    {
        var config = LoadConfig();
        var taskName = config.SelectedTasks.SingleOrDefault()
                       ?? throw new InvalidOperationException("请先选择一个 MaaNOP task。 ");
        var task = _project.Tasks.SingleOrDefault(candidate => candidate.Name == taskName)
                   ?? throw new InvalidDataException($"MaaNOP Config 选择的 task 不再存在：{taskName}。 ");
        var resolved = ProjectOptionResolver.Resolve(_project, task, config);
        var createdAtUtc = DateTime.UtcNow;
        var item = new RunPlanItem(
            Guid.NewGuid(),
            task.Name,
            task.Label,
            task.Entry,
            resolved.ResolvedTaskOptions,
            resolved.PipelineOverride);
        var plan = new RunPlan(
            ProtocolConstants.PlanVersion,
            createdAtUtc,
            _project.Provenance,
            _project.RuntimeProfileDigest,
            resolved.ResolvedGlobalOptions,
            [item]);
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(plan, ProtocolJson.Options);
        if (serializedBytes.Length > ProtocolConstants.MaximumRunPlanBytes)
        {
            throw new InvalidDataException(
                $"Run Plan 超过 {ProtocolConstants.MaximumRunPlanBytes} bytes：{serializedBytes.Length}。 ");
        }

        return new RunStartAttempt(Guid.NewGuid(), createdAtUtc, plan, CanonicalDigest.ComputePlanDigestV1(plan));
    }

    private static void ValidateConfigShape(MaaNopConfig config)
    {
        if (config.SchemaVersion != MaaNopConfig.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"首片只接受 SchemaVersion {MaaNopConfig.CurrentSchemaVersion} MaaNOP Config。 ");
        }
        if (config.SelectedTasks.Count > 1)
        {
            throw new InvalidDataException("首片临时 UI 只允许选择一个 top-level task。 ");
        }
        foreach (var (optionName, value) in config.ExplicitOptions)
        {
            if (string.IsNullOrWhiteSpace(optionName) || value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("ExplicitOptions key 必须为非空 option name，value 必须为 object。 ");
            }
        }
    }

    private MaaNopConfig LoadConfig()
    {
        var config = _configStore.Load();
        ValidateConfigShape(config);
        var selected = config.SelectedTasks.SingleOrDefault();
        if (selected is not null && !_project.Tasks.Any(task => task.Name == selected))
        {
            throw new InvalidDataException($"MaaNOP Config 选择的 task 不再存在：{selected}。 ");
        }
        return config;
    }

    private void ValidateActiveConfiguration(MaaNopConfig config)
    {
        var taskName = config.SelectedTasks.SingleOrDefault();
        if (taskName is not null)
        {
            _ = ProjectOptionResolver.Resolve(_project, FindTask(taskName), config);
            return;
        }

        ProjectOptionResolver.ValidateScope(_project, _project.GlobalOptions, config, "global_option");
    }

    private ProjectConfigurationView BuildConfiguration(MaaNopConfig config)
    {
        var global = BuildEditors(_project.GlobalOptions, config);
        var taskName = config.SelectedTasks.SingleOrDefault();
        var task = taskName is null
            ? []
            : BuildEditors(FindTask(taskName).Options, config);
        return new ProjectConfigurationView(global, task);
    }

    private IReadOnlyList<ProjectOptionEditor> BuildEditors(IReadOnlyList<string> names, MaaNopConfig config) =>
        names.Select(name => BuildEditor(name, config)).ToArray();

    private ProjectOptionEditor BuildEditor(string optionName, MaaNopConfig config)
    {
        var option = FindOption(optionName);
        if (option.Kind == OptionDefinitionKind.Input)
        {
            var explicitInputs = ExplicitOptionIntent.ReadInputs(option, config);
            var inputs = option.Inputs.Select(input => new ProjectInputEditor(
                input.Name,
                input.Label,
                input.Description,
                input.Default,
                explicitInputs.TryGetValue(input.Name, out var value) ? value : input.Default,
                explicitInputs.ContainsKey(input.Name),
                input.Verify,
                input.PatternMessage)).ToArray();
            return new ProjectOptionEditor(
                option.Name,
                option.Label,
                option.Description,
                ProjectOptionKind.Input,
                explicitInputs.Count != 0,
                null,
                null,
                [],
                inputs,
                []);
        }

        var explicitCase = ExplicitOptionIntent.ReadSelectedCase(option, config);
        var selectedCase = explicitCase ?? option.DefaultCase!;
        var selected = option.Cases.SingleOrDefault(item => item.Name == selectedCase)
            ?? throw new InvalidDataException(
                $"option {optionName} 的 case {selectedCase} 不存在。 ");
        var children = BuildEditors(selected.Options, config);
        return new ProjectOptionEditor(
            option.Name,
            option.Label,
            option.Description,
            option.Kind == OptionDefinitionKind.Switch
                ? ProjectOptionKind.Switch
                : ProjectOptionKind.Select,
            explicitCase is not null,
            selectedCase,
            option.DefaultCase,
            option.Cases.Select(item => new ProjectCaseEditor(
                item.Name,
                item.Label,
                item.Description)).ToArray(),
            [],
            children);
    }

    private TaskDefinition FindTask(string taskName) =>
        _project.Tasks.SingleOrDefault(task => task.Name == taskName)
        ?? throw new ArgumentException($"PI 中不存在 task：{taskName}。", nameof(taskName));

    private OptionDefinition FindOption(string optionName) =>
        _project.Options.GetValueOrDefault(optionName)
        ?? throw new ArgumentException($"PI 中不存在 option：{optionName}。", nameof(optionName));

    private static MaaNopConfig ReplaceExplicit(MaaNopConfig config, string optionName, JsonElement value)
    {
        var values = config.ExplicitOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values[optionName] = value;
        return config with { ExplicitOptions = values };
    }
}
