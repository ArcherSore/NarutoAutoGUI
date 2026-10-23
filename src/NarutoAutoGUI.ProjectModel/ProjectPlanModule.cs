using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

public sealed record ProjectTaskChoice(string Name, string Label, string Description)
{
    public IReadOnlyList<string> Groups { get; init; } = [];
    public PlanItemKind Kind { get; init; }
}

public sealed record ProjectTaskGroup(string Name, string Label, string Description, string? Icon, bool DefaultExpand);

public sealed record RunStartAttempt(Guid RunId, RunPlan Plan, string PlanDigest);

public sealed class ProjectPlanModule
{
    private readonly ProjectDefinition _project;
    private readonly MaaNopConfigStore _configStore;
    private MaaNopConfig _config;
    private string? _initializationWarning;
    private readonly HashSet<(Guid Configuration, PlanItemKind Kind, string Name)> _disabledItems = [];

    private ProjectPlanModule(ProjectDefinition project, MaaNopConfigStore configStore)
    {
        _project = project;
        _configStore = configStore;
        Tasks = project.Tasks
            .Select(task => new ProjectTaskChoice(task.Name, task.Label, task.Description) { Groups = task.Groups })
            .ToArray();

        Pretasks = project.Pretasks.Where(item => item.Applicable)
            .Select(item => new ProjectTaskChoice(item.Name, item.Label, item.Description) {
                Kind = PlanItemKind.Pretask
            }).ToArray();
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
    public IReadOnlyList<ProjectTaskChoice> Pretasks { get; }
    public IReadOnlyList<ProjectTaskGroup> Groups => _project.Groups;
    public IReadOnlyList<string> SelectedPretaskNames => LoadConfig().SelectedPretasks;
    public IReadOnlyList<ProjectTaskChoice> SelectedItems
    {
        get
        {
            var config = LoadConfig();
            var pretasks = Pretasks.Where(item => config.SelectedPretasks.Contains(item.Name, StringComparer.Ordinal));
            var missing = config.SelectedPretasks.Where(name => !_project.Pretasks.Any(item => item.Name == name))
                .Select(name => new ProjectTaskChoice(name, name, "前置任务已不在项目中，请移除。") {
                    Kind = PlanItemKind.Pretask
                });
            var tasks = config.SelectedTasks.Select(name => Tasks.SingleOrDefault(item => item.Name == name)
                ?? new ProjectTaskChoice(name, name, "任务已不在项目中，请移除。"));
            return pretasks.Concat(missing).Concat(tasks).ToArray();
        }
    }
    public int SelectedItemCount => SelectedItems.Count;
    public IReadOnlyList<ProjectTaskChoice> EnabledItems =>
        SelectedItems.Where(item => IsItemEnabled(item.Kind, item.Name)).ToArray();

    public bool IsItemEnabled(PlanItemKind kind, string name) =>
        !_disabledItems.Contains((ActiveConfigurationId, kind, name));

    // Temporary execution choices belong to this open workspace, never to the saved configuration.
    public void SetItemEnabled(PlanItemKind kind, string name, bool enabled)
    {
        if (!SelectedItems.Any(item => item.Kind == kind && item.Name == name)) {
            throw new ArgumentException("任务不在当前执行计划中。", nameof(name));
        }
        var key = (ActiveConfigurationId, kind, name);
        if (enabled) {
            _disabledItems.Remove(key);
        } else {
            _disabledItems.Add(key);
        }
    }

    private TaskConfiguration ExecutionConfiguration()
    {
        var config = LoadConfig();
        return config with {
            SelectedTasks = config.SelectedTasks.Where(name => IsItemEnabled(PlanItemKind.MaaTask, name)).ToArray(),
            SelectedPretasks = config.SelectedPretasks
                .Where(name => IsItemEnabled(PlanItemKind.Pretask, name)).ToArray()
        };
    }
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
        _disabledItems.RemoveWhere(item => item.Configuration == id);
        return true;
    }

    public void ValidateConfiguration() => ValidateActiveConfiguration(ExecutionConfiguration());

    public static ProjectPlanModule Open(string projectDirectory, string configPath, ProjectPlanModule? previous = null)
    {
        var project = ProjectInterfaceLoader.Load(projectDirectory);
        var module = new ProjectPlanModule(project, new MaaNopConfigStore(configPath));
        if (previous is not null) {
            module._disabledItems.UnionWith(previous._disabledItems.Where(item => module.Configurations.Any(config =>
                config.Id == item.Configuration && (item.Kind == PlanItemKind.Pretask
                    ? config.SelectedPretasks : config.SelectedTasks).Contains(item.Name, StringComparer.Ordinal))));
        }
        return module;
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
        _disabledItems.Remove((ActiveConfigurationId, PlanItemKind.MaaTask, taskName));
        return true;
    }

    public bool AddPretask(string name)
    {
        var pretask = FindPretask(name);
        if (!pretask.Applicable) {
            throw new InvalidOperationException($"前置任务 {name} 不适用于当前控制器或资源。");
        }
        var config = LoadConfig();
        if (config.SelectedPretasks.Contains(name, StringComparer.Ordinal)) {
            return false;
        }
        var updated = config with { SelectedPretasks = config.SelectedPretasks.Append(name).ToArray() };
        ValidateActiveConfiguration(updated);
        SaveConfiguration(updated);
        return true;
    }

    public bool RemovePretask(string name)
    {
        var config = LoadConfig();
        if (!config.SelectedPretasks.Contains(name, StringComparer.Ordinal)) {
            return false;
        }
        SaveConfiguration(config with {
            SelectedPretasks = config.SelectedPretasks.Where(item => item != name).ToArray()
        });
        _disabledItems.Remove((ActiveConfigurationId, PlanItemKind.Pretask, name));
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

    public ProjectConfigurationView GetConfiguration(string taskName, PlanItemKind kind = PlanItemKind.MaaTask)
    {
        var config = LoadConfig();
        var selected = kind == PlanItemKind.Pretask ? config.SelectedPretasks : config.SelectedTasks;
        if (!selected.Contains(taskName, StringComparer.Ordinal)) {
            throw new InvalidOperationException($"{kind} {taskName} 不在当前执行计划中。");
        }
        return kind == PlanItemKind.Pretask ? BuildPretaskConfiguration(config, FindPretask(taskName))
            : BuildConfiguration(config, taskName);
    }

    // Checks only the edited value, so a typo can be shown at its field instead of as a whole-config failure.
    public string? CheckInputValue(string optionName, string inputName, string value)
    {
        var input = FindOption(optionName).Inputs.SingleOrDefault(item => item.Name == inputName)
            ?? throw new ArgumentException($"option {optionName} 不包含 input {inputName}。", nameof(inputName));
        var typed = IsTypedOption(LoadConfig(), optionName);
        try {
            _ = ProjectInputValue.Parse(input, value, $"option {optionName} input {inputName} 的值", typed);
            return null;
        } catch (InvalidDataException exception) {
            return exception.Message;
        }
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
        var config = ExecutionConfiguration();
        ValidateActiveConfiguration(config);
        if (EnabledItems.Count == 0) {
            throw new InvalidOperationException("请先勾选要执行的任务或前置任务。");
        }

        var resolvedItems = config.SelectedTasks.Select(taskName => {
            var task = FindTask(taskName);
            return (Task: task, Resolved: ProjectOptionResolver.Resolve(_project, task, config));
        }).ToArray();
        var createdAtUtc = DateTime.UtcNow;
        var pretaskItems = SelectedPretaskDefinitions(config).Select(item => {
            var values = ProjectOptionResolver.ResolvePretask(_project, item, config);
            var arguments = item.Arguments.ToList();
            if (item.Options.Count != 0) {
                arguments.Add(JsonSerializer.Serialize(values, ProtocolJson.Options));
            }
            return new RunPlanItem(Guid.NewGuid(), item.Name, item.Label, string.Empty,
                values, ProtocolJson.ToElement(Array.Empty<object>())) {
                Kind = PlanItemKind.Pretask,
                Program = new PretaskCommand(ResolvePretaskExecutable(item.Executable), arguments)
            };
        });
        var items = pretaskItems.Concat(resolvedItems.Select(item => new RunPlanItem(
            Guid.NewGuid(), item.Task.Name, item.Task.Label, item.Task.Entry,
            item.Resolved.ResolvedTaskOptions, item.Resolved.PipelineOverride))).ToArray();
        var plan = new RunPlan(
            ProtocolConstants.PlanVersion, createdAtUtc,
            _project.Provenance, _project.RuntimeProfileDigest,
            resolvedItems.Length == 0 ? ProtocolJson.ToElement(new { })
                : resolvedItems[0].Resolved.ResolvedGlobalOptions, items);
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(plan, ProtocolJson.Options);
        if (serializedBytes.Length > ProtocolConstants.MaximumRunPlanBytes) {
            throw new InvalidDataException(
                $"Run Plan 超过 {ProtocolConstants.MaximumRunPlanBytes} bytes：{serializedBytes.Length}。 ");
        }

        return new RunStartAttempt(Guid.NewGuid(), plan, CanonicalDigest.ComputePlanDigestV2(plan));
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
        foreach (var name in config.SelectedPretasks) {
            var pretask = _project.Pretasks.SingleOrDefault(item => item.Name == name)
                ?? throw new InvalidDataException($"MaaNOP Config 选择的 pretask 不再存在：{name}。");
            if (pretask.Applicable) {
                _ = ProjectOptionResolver.ResolvePretask(_project, pretask, config);
            }
        }
        if (config.SelectedTasks.Count == 0 && config.SelectedPretasks.Count == 0) {
            ProjectOptionResolver.ValidateScope(_project, _project.GlobalOptions, config, "global_option");
        }
    }

    // Mirrors the validation above: a save converts inputs to their pipeline_type only in options a selected MaaTask
    // resolves through active cases, global options included unless only pretasks are selected.
    private bool IsTypedOption(TaskConfiguration config, string optionName)
    {
        IEnumerable<string> roots = config.SelectedTasks.Count == 0 && config.SelectedPretasks.Count != 0 ? []
            : _project.GlobalOptions.Concat(_project.Tasks
                .Where(task => config.SelectedTasks.Contains(task.Name, StringComparer.Ordinal))
                .SelectMany(task => task.Options));
        var pending = new Stack<string>(roots);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var name)) {
            if (name == optionName) {
                return true;
            }
            var option = _project.Options[name];
            if (!visited.Add(name) || option.Kind == OptionDefinitionKind.Input) {
                continue;
            }
            var selected = ExplicitOptionIntent.ReadSelectedCase(option, config) ?? option.DefaultCase;
            foreach (var nested in option.Cases.SingleOrDefault(item => item.Name == selected)?.Options ?? []) {
                pending.Push(nested);
            }
        }
        return false;
    }

    private ProjectConfigurationView BuildConfiguration(TaskConfiguration config)
    {
        var taskName = config.SelectedTasks.FirstOrDefault();
        if (taskName is not null) {
            return BuildConfiguration(config, taskName);
        }
        var pretask = SelectedPretaskDefinitions(config).FirstOrDefault();
        return pretask is null ? new ProjectConfigurationView([], []) : BuildPretaskConfiguration(config, pretask);
    }

    private ProjectConfigurationView BuildPretaskConfiguration(TaskConfiguration config, PretaskDefinition pretask) =>
        new([], BuildEditors(pretask.Options, config));

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

    private PretaskDefinition FindPretask(string name) =>
        _project.Pretasks.SingleOrDefault(item => item.Name == name)
        ?? throw new ArgumentException($"PI 中不存在 pretask：{name}。", nameof(name));

    // The pretasks a Run executes: selected, applicable, and in Project Interface order.
    private IEnumerable<PretaskDefinition> SelectedPretaskDefinitions(TaskConfiguration config) => _project.Pretasks
        .Where(item => item.Applicable && config.SelectedPretasks.Contains(item.Name, StringComparer.Ordinal));

    // The loader has rejected drive- and root-relative paths; a path with a separator is relative to the project root.
    private string ResolvePretaskExecutable(string executable)
    {
        return executable.Contains('/') || executable.Contains('\\')
            ? Path.GetFullPath(executable, _project.ProjectRoot) : executable;
    }

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
