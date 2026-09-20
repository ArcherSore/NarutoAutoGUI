using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

public sealed record ProjectTaskChoice(string Name, string Label, string Description);

public sealed record RunStartAttempt(Guid RunId, RunPlan Plan, string PlanDigest);

public sealed class ProjectPlanModule
{
    private readonly ProjectDefinition _project;
    private readonly MaaNopConfigStore _configStore;
    private MaaNopConfig _config;
    private string? _initializationWarning;

    private ProjectPlanModule(ProjectDefinition project, MaaNopConfigStore configStore)
    {
        _project = project;
        _configStore = configStore;
        Tasks = project.Tasks
            .Select(task => new ProjectTaskChoice(task.Name, task.Label, task.Description))
            .ToArray();

        _config = configStore.Load();
        if (configStore.WasMissing) {
            try {
                AddTask(Tasks[0].Name);
                InitializedTaskName = Tasks[0].Name;
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _initializationWarning = $"无法保存首次默认任务，已提供空配置。{exception.Message}";
            }
        }
    }

    public string ProjectName => _project.Provenance.Name;
    public string ProjectVersion => _project.Provenance.Version;
    public string RuntimeProfileDigest => _project.RuntimeProfileDigest;
    public string SourceInterfaceDigest => _project.Provenance.SourceInterfaceDigest;
    public IReadOnlyList<ProjectTaskChoice> Tasks { get; }
    public IReadOnlyList<string> SelectedTaskNames => LoadConfig().SelectedTasks;
    public Guid ActiveConfigurationId => _config.ActiveConfigurationId;
    public string? LoadWarning => _initializationWarning ?? _configStore.LoadWarning;
    public string? InitializedTaskName { get; }
    public IReadOnlyList<TaskConfiguration> Configurations => _config.Configurations;

    public Guid CreateConfiguration()
    {
        var configuration = new TaskConfiguration {
            Id = Guid.NewGuid(), Name = $"配置 {_config.Configurations.Count + 1}"
        };
        SaveConfig(_config with {
            ActiveConfigurationId = configuration.Id,
            Configurations = _config.Configurations.Append(configuration).ToArray()
        });
        return configuration.Id;
    }

    public void ActivateConfiguration(Guid id)
    {
        _ = FindConfiguration(id);
        if (id != ActiveConfigurationId) {
            SaveConfig(_config with { ActiveConfigurationId = id });
        }
    }

    public void RenameConfiguration(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new InvalidDataException("配置名称不能为空。");
        }
        var configuration = FindConfiguration(id);
        if (configuration.Name != name.Trim()) {
            SaveConfiguration(configuration with { Name = name.Trim() });
        }
    }

    public bool DeleteConfiguration(Guid id)
    {
        var index = _config.Configurations.ToList().FindIndex(item => item.Id == id);
        if (index < 0 || _config.Configurations.Count == 1) {
            return false;
        }
        var active = id == ActiveConfigurationId
            ? _config.Configurations[index == 0 ? 1 : index - 1].Id : ActiveConfigurationId;
        SaveConfig(_config with {
            ActiveConfigurationId = active,
            Configurations = _config.Configurations.Where(item => item.Id != id).ToArray()
        });
        return true;
    }

    public void ValidateConfiguration() => ValidateActiveConfiguration(LoadConfig());

    public static ProjectPlanModule Open(string projectDirectory, string configPath)
    {
        var project = ProjectInterfaceLoader.Load(projectDirectory);
        return new ProjectPlanModule(project, new MaaNopConfigStore(configPath));
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
        SaveConfiguration(updated);
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
        SaveConfiguration(updated);
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
        SaveConfiguration(updated);
        return true;
    }

    public ProjectConfigurationView GetConfiguration(string taskName)
    {
        _ = FindTask(taskName);
        var config = LoadConfig();
        if (!config.SelectedTasks.Contains(taskName, StringComparer.Ordinal)) {
            throw new InvalidOperationException($"task {taskName} 不在当前执行计划中。 ");
        }
        return BuildConfiguration(config, taskName);
    }

    public ProjectConfigurationView SetInputValue(string optionName, string inputName, string value)
        => SetInputValue(ActiveConfigurationId, optionName, inputName, value);

    public ProjectConfigurationView SetInputValue(
        Guid configurationId, string optionName, string inputName, string value)
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

        var config = FindConfiguration(configurationId);
        var values = ExplicitOptionIntent.ReadInputs(option, config)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values[inputName] = value;
        var updated = ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateInputs(values));
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
        return BuildConfiguration(updated);
    }

    public ProjectConfigurationView SetSelectedCase(string optionName, string selectedCase)
        => SetSelectedCase(ActiveConfigurationId, optionName, selectedCase);

    public ProjectConfigurationView SetSelectedCase(Guid configurationId, string optionName, string selectedCase)
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

        var config = FindConfiguration(configurationId);
        var updated = ReplaceExplicit(config, optionName, ExplicitOptionIntent.CreateSelectedCase(selectedCase));
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
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

        return new RunStartAttempt(Guid.NewGuid(), plan, CanonicalDigest.ComputePlanDigestV1(plan));
    }

    private TaskConfiguration LoadConfig() => FindConfiguration(ActiveConfigurationId);

    private TaskConfiguration FindConfiguration(Guid id) =>
        _config.Configurations.SingleOrDefault(item => item.Id == id)
        ?? throw new InvalidOperationException("目标配置已不存在。");

    private void SaveConfiguration(TaskConfiguration configuration)
    {
        var updated = _config with {
            Configurations = _config.Configurations.Select(item => item.Id == configuration.Id ? configuration : item)
                .ToArray()
        };
        SaveConfig(updated);
    }

    private void SaveConfig(MaaNopConfig config)
    {
        _configStore.Save(config);
        _config = config;
        _initializationWarning = null;
    }

    private void ValidateActiveConfiguration(TaskConfiguration config)
    {
        ValidateSelectedTasks(config);
        foreach (var taskName in config.SelectedTasks) {
            _ = ProjectOptionResolver.Resolve(_project, FindTask(taskName), config);
        }
        if (config.SelectedTasks.Count == 0) {
            ProjectOptionResolver.ValidateScope(_project, _project.GlobalOptions, config, "global_option");
        }
    }

    private ProjectConfigurationView BuildConfiguration(TaskConfiguration config)
    {
        var taskName = config.SelectedTasks.FirstOrDefault();
        return BuildConfiguration(config, taskName);
    }

    private ProjectConfigurationView BuildConfiguration(TaskConfiguration config, string? taskName)
    {
        var global = BuildEditors(_project.GlobalOptions, config);
        var task = taskName is null
            ? []
            : BuildEditors(FindTask(taskName).Options, config);
        return new ProjectConfigurationView(global, task);
    }

    private void ValidateSelectedTasks(TaskConfiguration config)
    {
        foreach (var taskName in config.SelectedTasks) {
            if (!_project.Tasks.Any(task => task.Name == taskName)) {
                throw new InvalidDataException($"MaaNOP Config 选择的 task 不再存在：{taskName}。 ");
            }
        }
    }

    private IReadOnlyList<ProjectOptionEditor> BuildEditors(IReadOnlyList<string> names, TaskConfiguration config) =>
        names.Select(name => BuildEditor(name, config)).ToArray();

    private ProjectOptionEditor BuildEditor(string optionName, TaskConfiguration config)
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

    private static TaskConfiguration ReplaceExplicit(TaskConfiguration config, string optionName, JsonElement value)
    {
        var values = config.ExplicitOptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        values[optionName] = value;
        return config with { ExplicitOptions = values };
    }
}
