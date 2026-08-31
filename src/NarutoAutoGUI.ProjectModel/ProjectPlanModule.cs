using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

public sealed record ProjectTaskChoice(string Name, string Label, string Description);

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
            .Select(task => new ProjectTaskChoice(task.Name, task.Label, task.Description))
            .ToArray();

        var config = configStore.Load();
        ValidateConfigShape(config);
        ValidateSelectedTasks(config);
        SelectedTaskNames = config.SelectedTasks.ToArray();
    }

    public string ProjectDirectory => _project.ProjectRoot;
    public string ProjectName => _project.Provenance.Name;
    public string ProjectVersion => _project.Provenance.Version;
    public string RuntimeProfileDigest => _project.RuntimeProfileDigest;
    public string SourceInterfaceDigest => _project.Provenance.SourceInterfaceDigest;
    public IReadOnlyList<ProjectTaskChoice> Tasks { get; }
    public IReadOnlyList<string> SelectedTaskNames { get; private set; }
    public string? SelectedTaskName => SelectedTaskNames.Count == 1 ? SelectedTaskNames[0] : null;

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
        var updated = current with {
            SelectedTasks = [taskName]
        };
        _ = ProjectOptionResolver.Resolve(_project, FindTask(taskName), updated);
        _configStore.Save(updated);
        SelectedTaskNames = [taskName];
    }

    public bool AddTask(string taskName)
    {
        _ = FindTask(taskName);
        var config = LoadConfig();
        if (config.SelectedTasks.Contains(taskName, StringComparer.Ordinal)) {
            return false;
        }

        var updated = config with { SelectedTasks = config.SelectedTasks.Append(taskName).ToArray() };
        ValidateActiveConfiguration(updated);
        _configStore.Save(updated);
        SelectedTaskNames = updated.SelectedTasks;
        return true;
    }

    public bool RemoveTask(string taskName)
    {
        var config = LoadConfig();
        if (!config.SelectedTasks.Contains(taskName, StringComparer.Ordinal)) {
            return false;
        }

        var updated = config with {
            SelectedTasks = config.SelectedTasks.Where(name => name != taskName).ToArray()
        };
        ValidateActiveConfiguration(updated);
        _configStore.Save(updated);
        SelectedTaskNames = updated.SelectedTasks;
        return true;
    }

    public bool MoveTask(string taskName, int targetIndex)
    {
        var config = LoadConfig();
        var currentIndex = config.SelectedTasks.ToList().IndexOf(taskName);
        if (currentIndex < 0) {
            throw new ArgumentException($"执行计划中不存在 task：{taskName}。", nameof(taskName));
        }
        if (targetIndex < 0 || targetIndex >= config.SelectedTasks.Count) {
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        }
        if (currentIndex == targetIndex) {
            return false;
        }

        var selected = config.SelectedTasks.ToList();
        selected.RemoveAt(currentIndex);
        selected.Insert(targetIndex, taskName);
        var updated = config with { SelectedTasks = selected };
        ValidateActiveConfiguration(updated);
        _configStore.Save(updated);
        SelectedTaskNames = updated.SelectedTasks;
        return true;
    }

    public ProjectConfigurationView GetConfiguration()
    {
        var config = LoadConfig();
        ValidateActiveConfiguration(config);
        return BuildConfiguration(config);
    }

    public ProjectConfigurationView GetConfiguration(string taskName)
    {
        _ = FindTask(taskName);
        var config = LoadConfig();
        if (!config.SelectedTasks.Contains(taskName, StringComparer.Ordinal)) {
            throw new InvalidOperationException($"task {taskName} 不在当前执行计划中。 ");
        }
        ValidateActiveConfiguration(config);
        return BuildConfiguration(config, taskName);
    }

    public ProjectConfigurationView SetInputValue(string optionName, string inputName, string value)
    {
        var option = FindOption(optionName);
        if (option.Kind != OptionDefinitionKind.Input) {
            throw new ArgumentException($"option {optionName} 不是 input。", nameof(optionName));
        }
        if (!option.Inputs.Any(input => input.Name == inputName)) {
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
        if (option.Kind is not (OptionDefinitionKind.Select or OptionDefinitionKind.Switch)) {
            throw new ArgumentException(
                $"option {optionName} 不是 select/switch。",
                nameof(optionName));
        }
        if (!option.Cases.Any(item => item.Name == selectedCase)) {
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
        ProtocolConstants.LaunchContextVersion, workerInstanceId,
        _project.RuntimeProfileDigest, _project.ProjectRoot, _project.Provenance,
        _project.Controller, _project.Resources, _project.Agent);

    public RunStartAttempt CreateRunStartAttempt()
    {
        var config = LoadConfig();
        if (config.SelectedTasks.Count == 0) {
            throw new InvalidOperationException("请先向执行计划添加 MaaNOP task。 ");
        }

        var resolvedItems = config.SelectedTasks.Select(taskName => {
            var task = FindTask(taskName);
            return (Task: task, Resolved: ProjectOptionResolver.Resolve(_project, task, config));
        }).ToArray();
        var createdAtUtc = DateTime.UtcNow;
        var items = resolvedItems.Select(item => new RunPlanItem(
            Guid.NewGuid(), item.Task.Name, item.Task.Label, item.Task.Entry,
            item.Resolved.ResolvedTaskOptions, item.Resolved.PipelineOverride)).ToArray();
        var plan = new RunPlan(
            ProtocolConstants.PlanVersion, createdAtUtc,
            _project.Provenance, _project.RuntimeProfileDigest,
            resolvedItems[0].Resolved.ResolvedGlobalOptions, items);
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(plan, ProtocolJson.Options);
        if (serializedBytes.Length > ProtocolConstants.MaximumRunPlanBytes) {
            throw new InvalidDataException(
                $"Run Plan 超过 {ProtocolConstants.MaximumRunPlanBytes} bytes：{serializedBytes.Length}。 ");
        }

        return new RunStartAttempt(Guid.NewGuid(), createdAtUtc, plan, CanonicalDigest.ComputePlanDigestV1(plan));
    }

    private static void ValidateConfigShape(MaaNopConfig config)
    {
        if (config.SchemaVersion != MaaNopConfig.CurrentSchemaVersion) {
            throw new InvalidDataException(
                $"首片只接受 SchemaVersion {MaaNopConfig.CurrentSchemaVersion} MaaNOP Config。 ");
        }
        if (config.SelectedTasks.Count != config.SelectedTasks.Distinct(StringComparer.Ordinal).Count()) {
            throw new InvalidDataException("SelectedTasks 不能包含重复 task；当前不支持同一 Task 多实例。 ");
        }
        foreach (var (optionName, value) in config.ExplicitOptions) {
            if (string.IsNullOrWhiteSpace(optionName) || value.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException("ExplicitOptions key 必须为非空 option name，value 必须为 object。 ");
            }
        }
    }

    private MaaNopConfig LoadConfig()
    {
        var config = _configStore.Load();
        ValidateConfigShape(config);
        ValidateSelectedTasks(config);
        return config;
    }

    private void ValidateActiveConfiguration(MaaNopConfig config)
    {
        foreach (var taskName in config.SelectedTasks) {
            _ = ProjectOptionResolver.Resolve(_project, FindTask(taskName), config);
        }
        ProjectOptionResolver.ValidateScope(_project, _project.GlobalOptions, config, "global_option");
    }

    private ProjectConfigurationView BuildConfiguration(MaaNopConfig config)
    {
        var taskName = config.SelectedTasks.FirstOrDefault();
        return BuildConfiguration(config, taskName);
    }

    private ProjectConfigurationView BuildConfiguration(MaaNopConfig config, string? taskName)
    {
        var global = BuildEditors(_project.GlobalOptions, config);
        var task = taskName is null
            ? []
            : BuildEditors(FindTask(taskName).Options, config);
        return new ProjectConfigurationView(global, task);
    }

    private void ValidateSelectedTasks(MaaNopConfig config)
    {
        foreach (var taskName in config.SelectedTasks) {
            if (!_project.Tasks.Any(task => task.Name == taskName)) {
                throw new InvalidDataException($"MaaNOP Config 选择的 task 不再存在：{taskName}。 ");
            }
        }
    }

    private IReadOnlyList<ProjectOptionEditor> BuildEditors(IReadOnlyList<string> names, MaaNopConfig config) =>
        names.Select(name => BuildEditor(name, config)).ToArray();

    private ProjectOptionEditor BuildEditor(string optionName, MaaNopConfig config)
    {
        var option = FindOption(optionName);
        if (option.Kind == OptionDefinitionKind.Input) {
            var explicitInputs = ExplicitOptionIntent.ReadInputs(option, config);
            var inputs = option.Inputs.Select(input => new ProjectInputEditor(
                input.Name, input.Label, input.Description, input.Default,
                explicitInputs.TryGetValue(input.Name, out var value) ? value : input.Default,
                explicitInputs.ContainsKey(input.Name), input.Verify, input.PatternMessage)).ToArray();
            return new ProjectOptionEditor(
                option.Name, option.Label, option.Description, ProjectOptionKind.Input,
                explicitInputs.Count != 0, null, null, [], inputs, []);
        }

        var explicitCase = ExplicitOptionIntent.ReadSelectedCase(option, config);
        var selectedCase = explicitCase ?? option.DefaultCase!;
        var selected = option.Cases.SingleOrDefault(item => item.Name == selectedCase)
            ?? throw new InvalidDataException(
                $"option {optionName} 的 case {selectedCase} 不存在。 ");
        var children = BuildEditors(selected.Options, config);
        return new ProjectOptionEditor(
            option.Name, option.Label, option.Description,
            option.Kind == OptionDefinitionKind.Switch
                ? ProjectOptionKind.Switch
                : ProjectOptionKind.Select,
            explicitCase is not null, selectedCase, option.DefaultCase,
            option.Cases.Select(item => new ProjectCaseEditor(
                item.Name, item.Label, item.Description)).ToArray(),
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
