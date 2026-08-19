using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

public sealed record ProjectTaskChoice(
    string Name,
    string Label,
    bool DefaultOnlyValid,
    string? ValidationError);

public sealed record RunStartAttempt(
    Guid RunId,
    DateTime CreatedAtUtc,
    RunPlan Plan,
    string PlanDigest);

public sealed class ProjectPlanModule
{
    private readonly ProjectDefinition _project;
    private readonly MaaNopConfigStore _configStore;

    private ProjectPlanModule(ProjectDefinition project, MaaNopConfigStore configStore)
    {
        _project = project;
        _configStore = configStore;
        Tasks = project.Tasks.Select(task =>
        {
            try
            {
                _ = DefaultOptionResolver.Resolve(project, task);
                return new ProjectTaskChoice(task.Name, task.Label, true, null);
            }
            catch (Exception exception) when (exception is InvalidDataException
                                                   or JsonException
                                                   or ArgumentException)
            {
                return new ProjectTaskChoice(
                    task.Name,
                    task.Label,
                    false,
                    exception.GetBaseException().Message);
            }
        }).ToArray();

        var config = configStore.Load();
        ValidateConfigShape(config);
        SelectedTaskName = config.SelectedTasks.SingleOrDefault();
        if (SelectedTaskName is not null
            && !project.Tasks.Any(task => task.Name == SelectedTaskName))
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
        var task = Tasks.SingleOrDefault(candidate => candidate.Name == taskName)
                   ?? throw new ArgumentException($"PI 中不存在 task：{taskName}。", nameof(taskName));
        if (!task.DefaultOnlyValid)
        {
            throw new InvalidOperationException(
                $"task {taskName} 无法使用纯默认 option：{task.ValidationError}");
        }

        _configStore.Save(new MaaNopConfig
        {
            SelectedTasks = [taskName],
            ExplicitOptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        });
        SelectedTaskName = taskName;
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
        var config = _configStore.Load();
        ValidateConfigShape(config);
        var taskName = config.SelectedTasks.SingleOrDefault()
                       ?? throw new InvalidOperationException("请先选择一个 MaaNOP task。 ");
        var task = _project.Tasks.SingleOrDefault(candidate => candidate.Name == taskName)
                   ?? throw new InvalidDataException($"MaaNOP Config 选择的 task 不再存在：{taskName}。 ");
        var resolved = DefaultOptionResolver.Resolve(_project, task);
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

        return new RunStartAttempt(
            Guid.NewGuid(),
            createdAtUtc,
            plan,
            CanonicalDigest.ComputePlanDigestV1(plan));
    }

    private static void ValidateConfigShape(MaaNopConfig config)
    {
        if (config.SchemaVersion != 1)
        {
            throw new InvalidDataException("首片只接受 SchemaVersion 1 MaaNOP Config。 ");
        }
        if (config.SelectedTasks.Count > 1)
        {
            throw new InvalidDataException("首片临时 UI 只允许选择一个 top-level task。 ");
        }
        if (config.ExplicitOptions.Count != 0)
        {
            throw new InvalidDataException("首片要求 ExplicitOptions 为空并完全使用 PI 默认语义。 ");
        }
    }
}

internal sealed record ProjectDefinition(
    string ProjectRoot,
    ProjectProvenance Provenance,
    Win32ControllerDefinition Controller,
    IReadOnlyList<ResourceDefinition> Resources,
    AgentDefinition Agent,
    string RuntimeProfileDigest,
    IReadOnlyList<string> GlobalOptions,
    IReadOnlyList<string> ResourceOptions,
    IReadOnlyList<string> ControllerOptions,
    IReadOnlyList<TaskDefinition> Tasks,
    IReadOnlyDictionary<string, OptionDefinition> Options);

internal sealed record TaskDefinition(
    string Name,
    string Label,
    string Entry,
    IReadOnlyList<string> Options,
    JsonElement PipelineOverride);

internal sealed record OptionDefinition(
    string Name,
    string Type,
    string? DefaultCase,
    IReadOnlyList<InputDefinition> Inputs,
    IReadOnlyList<CaseDefinition> Cases,
    JsonElement PipelineOverride);

internal sealed record InputDefinition(
    string Name,
    string Default,
    string PipelineType,
    string? Verify);

internal sealed record CaseDefinition(
    string Name,
    IReadOnlyList<string> Options,
    JsonElement PipelineOverride);

internal sealed record ResolvedProjectOptions(
    JsonElement ResolvedGlobalOptions,
    JsonElement ResolvedTaskOptions,
    JsonElement PipelineOverride);

internal static class ProjectInterfaceLoader
{
    private static readonly HashSet<string> AllowedTopLevel = new(StringComparer.Ordinal)
    {
        "interface_version", "name", "label", "version", "description", "icon",
        "controller", "resource", "agent", "global_option", "task", "option"
    };

    internal static ProjectDefinition Load(string projectDirectory)
    {
        var projectRoot = PathCanonicalizerV1.Canonicalize(projectDirectory);
        var interfacePath = Path.Combine(projectRoot, "interface.json");
        if (!File.Exists(interfacePath))
        {
            throw new FileNotFoundException(
                "MaaNOP Project Directory 必须直接包含 interface.json。",
                interfacePath);
        }

        var bytes = File.ReadAllBytes(interfacePath);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var root = RequireObject(document.RootElement, "$");
        RejectUnknownProperties(root, AllowedTopLevel, "$");
        var interfaceVersion = RequireInt(root, "interface_version", "$");
        if (interfaceVersion != 2)
        {
            throw new InvalidDataException($"只支持 interface_version=2，实际为 {interfaceVersion}。 ");
        }

        var controllers = RequireArray(root, "controller", "$").EnumerateArray().ToArray();
        if (controllers.Length != 1)
        {
            throw new InvalidDataException("首版要求恰好一个 Win32 controller。 ");
        }
        var controller = ParseController(controllers[0]);

        var resourceElements = RequireArray(root, "resource", "$").EnumerateArray().ToArray();
        if (resourceElements.Length != 1)
        {
            throw new InvalidDataException("首版要求恰好一个 resource。 ");
        }
        var (resources, resourceOptions) = ParseResources(resourceElements, projectRoot);
        var (agentExec, agentArgs) = ParseAgent(RequireProperty(root, "agent", "$"));
        var agent = new AgentDefinition(agentExec, agentArgs, projectRoot);
        var tasks = ParseTasks(RequireArray(root, "task", "$"));
        var options = ParseOptions(RequireProperty(root, "option", "$"));
        var globalOptions = ReadStringArray(root, "global_option", required: false, "$");
        ValidateReferences(globalOptions, resourceOptions, controller.Options, tasks, options);

        var provenance = new ProjectProvenance(
            RequireString(root, "name", "$"),
            OptionalString(root, "version") ?? string.Empty,
            interfaceVersion,
            CanonicalDigest.ComputeSourceInterfaceDigest(bytes));
        var runtimeProfileDigest = CanonicalDigest.ComputeRuntimeProfileDigestV1(
            projectRoot,
            controller.Definition,
            resources,
            agent);
        return new ProjectDefinition(
            projectRoot,
            provenance,
            controller.Definition,
            resources,
            agent,
            runtimeProfileDigest,
            globalOptions,
            resourceOptions,
            controller.Options,
            tasks,
            options);
    }

    private static (Win32ControllerDefinition Definition, IReadOnlyList<string> Options) ParseController(
        JsonElement element)
    {
        var obj = RequireObject(element, "$.controller[0]");
        RejectUnknownProperties(
            obj,
            ["name", "label", "description", "icon", "type", "win32", "option"],
            "$.controller[0]");
        if (RequireString(obj, "type", "$.controller[0]") != "Win32")
        {
            throw new InvalidDataException("首版 controller.type 必须为 Win32。 ");
        }
        var win32 = RequireObject(RequireProperty(obj, "win32", "$.controller[0]"), "$.controller[0].win32");
        RejectUnknownProperties(
            win32,
            ["class_regex", "window_regex", "screencap", "mouse", "keyboard"],
            "$.controller[0].win32");
        return (
            new Win32ControllerDefinition(
                RequireString(obj, "name", "$.controller[0]"),
                RequireString(win32, "class_regex", "$.controller[0].win32"),
                RequireString(win32, "window_regex", "$.controller[0].win32"),
                RequireString(win32, "screencap", "$.controller[0].win32"),
                RequireString(win32, "mouse", "$.controller[0].win32"),
                RequireString(win32, "keyboard", "$.controller[0].win32")),
            ReadStringArray(obj, "option", required: false, "$.controller[0]"));
    }

    private static (IReadOnlyList<ResourceDefinition> Resources, IReadOnlyList<string> Options) ParseResources(
        IReadOnlyList<JsonElement> elements,
        string projectRoot)
    {
        var resources = new List<ResourceDefinition>();
        var allOptions = new List<string>();
        for (var index = 0; index < elements.Count; index++)
        {
            var path = $"$.resource[{index}]";
            var obj = RequireObject(elements[index], path);
            RejectUnknownProperties(
                obj,
                ["name", "label", "description", "icon", "path", "controller", "option"],
                path);
            var resourcePaths = ReadStringArray(obj, "path", required: true, path)
                .Select(value => PathCanonicalizerV1.Canonicalize(
                    Path.IsPathFullyQualified(value) ? value : Path.Combine(projectRoot, value)))
                .ToArray();
            resources.Add(new ResourceDefinition(RequireString(obj, "name", path), resourcePaths));
            allOptions.AddRange(ReadStringArray(obj, "option", required: false, path));
        }
        return (resources, allOptions);
    }

    private static (string ChildExec, IReadOnlyList<string> ChildArgs) ParseAgent(JsonElement element)
    {
        var obj = RequireObject(element, "$.agent");
        RejectUnknownProperties(obj, ["child_exec", "child_args", "label", "description", "icon"], "$.agent");
        return (
            RequireString(obj, "child_exec", "$.agent"),
            ReadStringArray(obj, "child_args", required: false, "$.agent"));
    }

    private static IReadOnlyList<TaskDefinition> ParseTasks(JsonElement array)
    {
        var tasks = new List<TaskDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"$.task[{index++}]";
            var obj = RequireObject(element, path);
            RejectUnknownProperties(
                obj,
                ["name", "label", "description", "icon", "entry", "option", "pipeline_override", "controller", "resource"],
                path);
            var name = RequireString(obj, "name", path);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"重复 task.name：{name}。 ");
            }
            tasks.Add(new TaskDefinition(
                name,
                OptionalString(obj, "label") ?? name,
                RequireString(obj, "entry", path),
                ReadStringArray(obj, "option", required: false, path),
                ReadObjectOrEmpty(obj, "pipeline_override", path)));
        }
        if (tasks.Count == 0)
        {
            throw new InvalidDataException("PI 至少需要一个 task。 ");
        }
        return tasks;
    }

    private static IReadOnlyDictionary<string, OptionDefinition> ParseOptions(JsonElement element)
    {
        var obj = RequireObject(element, "$.option");
        var result = new Dictionary<string, OptionDefinition>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            var path = $"$.option.{property.Name}";
            var option = RequireObject(property.Value, path);
            RejectUnknownProperties(
                option,
                ["type", "label", "description", "icon", "default_case", "cases", "inputs", "pipeline_override", "controller", "resource"],
                path);
            var type = OptionalString(option, "type") ?? "select";
            if (type is not ("select" or "switch" or "input"))
            {
                throw new InvalidDataException($"首版不支持 option type：{type}（{path}）。 ");
            }
            var inputs = ParseInputs(option, path);
            var cases = ParseCases(option, path);
            result.Add(property.Name, new OptionDefinition(
                property.Name,
                type,
                OptionalString(option, "default_case"),
                inputs,
                cases,
                ReadObjectOrEmpty(option, "pipeline_override", path)));
        }
        return result;
    }

    private static IReadOnlyList<InputDefinition> ParseInputs(JsonElement option, string path)
    {
        if (!option.TryGetProperty("inputs", out var array))
        {
            return [];
        }
        RequireArrayValue(array, $"{path}.inputs");
        var result = new List<InputDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var inputPath = $"{path}.inputs[{index++}]";
            var input = RequireObject(element, inputPath);
            RejectUnknownProperties(
                input,
                ["name", "label", "description", "icon", "default", "pipeline_type", "verify", "pattern_msg"],
                inputPath);
            var name = RequireString(input, "name", inputPath);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"重复 input name：{name}（{inputPath}）。 ");
            }
            if (!input.TryGetProperty("default", out var defaultValue)
                || defaultValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"default-only slice 要求 {inputPath}.default 为 string。 ");
            }
            var pipelineType = OptionalString(input, "pipeline_type") ?? "string";
            if (pipelineType is not ("string" or "int" or "bool"))
            {
                throw new InvalidDataException($"不支持 pipeline_type={pipelineType}（{inputPath}）。 ");
            }
            result.Add(new InputDefinition(
                name,
                defaultValue.GetString()!,
                pipelineType,
                OptionalString(input, "verify")));
        }
        return result;
    }

    private static IReadOnlyList<CaseDefinition> ParseCases(JsonElement option, string path)
    {
        if (!option.TryGetProperty("cases", out var array))
        {
            return [];
        }
        RequireArrayValue(array, $"{path}.cases");
        var result = new List<CaseDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var casePath = $"{path}.cases[{index++}]";
            var item = RequireObject(element, casePath);
            RejectUnknownProperties(
                item,
                ["name", "label", "description", "icon", "option", "pipeline_override"],
                casePath);
            var name = RequireString(item, "name", casePath);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"重复 case.name：{name}（{casePath}）。 ");
            }
            result.Add(new CaseDefinition(
                name,
                ReadStringArray(item, "option", required: false, casePath),
                ReadObjectOrEmpty(item, "pipeline_override", casePath)));
        }
        return result;
    }

    private static void ValidateReferences(
        IReadOnlyList<string> global,
        IReadOnlyList<string> resource,
        IReadOnlyList<string> controller,
        IReadOnlyList<TaskDefinition> tasks,
        IReadOnlyDictionary<string, OptionDefinition> options)
    {
        foreach (var reference in global.Concat(resource).Concat(controller)
                     .Concat(tasks.SelectMany(task => task.Options))
                     .Concat(options.Values.SelectMany(option => option.Cases.SelectMany(item => item.Options))))
        {
            if (!options.ContainsKey(reference))
            {
                throw new InvalidDataException($"PI 引用了不存在的 option：{reference}。 ");
            }
        }
    }

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path} 必须是 object。 ");
        }
        return element;
    }

    private static JsonElement RequireArray(JsonElement obj, string name, string path)
    {
        var value = RequireProperty(obj, name, path);
        RequireArrayValue(value, $"{path}.{name}");
        return value;
    }

    private static void RequireArrayValue(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path} 必须是 array。 ");
        }
    }

    private static JsonElement RequireProperty(JsonElement obj, string name, string path)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"缺少 {path}.{name}。 ");
        }
        return value;
    }

    private static string RequireString(JsonElement obj, string name, string path) =>
        OptionalString(obj, name)
        ?? throw new InvalidDataException($"{path}.{name} 必须是非空 string。 ");

    private static string? OptionalString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{name} 必须是非空 string。 ");
        }
        return value.GetString();
    }

    private static int RequireInt(JsonElement obj, string name, string path)
    {
        var value = RequireProperty(obj, name, path);
        if (!value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"{path}.{name} 必须是 int。 ");
        }
        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement obj,
        string name,
        bool required,
        string path)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return required
                ? throw new InvalidDataException($"缺少 {path}.{name}。 ")
                : [];
        }
        RequireArrayValue(value, $"{path}.{name}");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException($"{path}.{name} 只能包含非空 string。 ");
            }
            result.Add(item.GetString()!);
        }
        return result;
    }

    private static JsonElement ReadObjectOrEmpty(JsonElement obj, string name, string path)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return ProtocolJson.ToElement(new { });
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path}.{name} 必须是 object。 ");
        }
        return value.Clone();
    }

    private static void RejectUnknownProperties(
        JsonElement obj,
        IEnumerable<string> allowed,
        string path)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                throw new InvalidDataException($"首版不支持执行字段 {path}.{property.Name}。 ");
            }
        }
    }
}

internal static class DefaultOptionResolver
{
    internal static ResolvedProjectOptions Resolve(ProjectDefinition project, TaskDefinition task)
    {
        var merged = new JsonObject();
        MergeObject(merged, JsonNode.Parse(task.PipelineOverride.GetRawText())!.AsObject());

        var globalValues = new JsonObject();
        var taskValues = new JsonObject();
        ResolveScope(project, project.GlobalOptions, globalValues, merged, "global_option");
        ResolveScope(project, project.ResourceOptions, new JsonObject(), merged, "resource.option");
        ResolveScope(project, project.ControllerOptions, new JsonObject(), merged, "controller.option");
        ResolveScope(project, task.Options, taskValues, merged, $"task.{task.Name}.option");
        return new ResolvedProjectOptions(
            ToElement(globalValues),
            ToElement(taskValues),
            ToElement(merged));
    }

    private static void ResolveScope(
        ProjectDefinition project,
        IReadOnlyList<string> optionNames,
        JsonObject resolvedValues,
        JsonObject mergedPipeline,
        string scope)
    {
        foreach (var optionName in optionNames)
        {
            ResolveOption(
                project,
                optionName,
                resolvedValues,
                mergedPipeline,
                new HashSet<string>(StringComparer.Ordinal),
                scope);
        }
    }

    private static void ResolveOption(
        ProjectDefinition project,
        string optionName,
        JsonObject resolvedValues,
        JsonObject mergedPipeline,
        HashSet<string> stack,
        string scope)
    {
        if (!stack.Add(optionName))
        {
            throw new InvalidDataException($"option 递归引用形成循环：{string.Join(" -> ", stack)} -> {optionName}。 ");
        }
        var option = project.Options[optionName];
        try
        {
            switch (option.Type)
            {
                case "input":
                {
                    if (option.Inputs.Count == 0)
                    {
                        throw new InvalidDataException($"input option {optionName} 没有 inputs。 ");
                    }
                    var values = new JsonObject();
                    var substitutions = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                    foreach (var input in option.Inputs)
                    {
                        if (input.Verify is not null)
                        {
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
                                    $"option {optionName} input {input.Name} 的 verify 非法。",
                                    exception);
                            }
                            if (!regex.IsMatch(input.Default))
                            {
                                throw new InvalidDataException(
                                    $"option {optionName} input {input.Name} 的默认值未通过 verify。 ");
                            }
                        }
                        values[input.Name] = input.Default;
                        substitutions[input.Name] = ConvertPipelineValue(optionName, input);
                    }
                    resolvedValues[optionName] = values;
                    MergeTemplated(mergedPipeline, option.PipelineOverride, substitutions);
                    break;
                }
                case "select":
                case "switch":
                {
                    if (option.DefaultCase is null)
                    {
                        throw new InvalidDataException($"option {optionName} 缺少 default_case。 ");
                    }
                    var selected = option.Cases.SingleOrDefault(item => item.Name == option.DefaultCase)
                                   ?? throw new InvalidDataException(
                                       $"option {optionName} 的 default_case {option.DefaultCase} 不存在。 ");
                    if (option.Type == "switch" && option.Cases.Count != 2)
                    {
                        throw new InvalidDataException($"switch option {optionName} 必须恰好有两个 case。 ");
                    }
                    resolvedValues[optionName] = selected.Name;
                    MergeObject(
                        mergedPipeline,
                        JsonNode.Parse(option.PipelineOverride.GetRawText())!.AsObject());
                    MergeObject(
                        mergedPipeline,
                        JsonNode.Parse(selected.PipelineOverride.GetRawText())!.AsObject());
                    foreach (var nested in selected.Options)
                    {
                        ResolveOption(project, nested, resolvedValues, mergedPipeline, stack, scope);
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"不支持 option type：{option.Type}。 ");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new InvalidDataException($"{scope} 默认解析 {optionName} 失败：{exception.Message}", exception);
        }
        finally
        {
            stack.Remove(optionName);
        }
    }

    private static JsonNode? ConvertPipelineValue(string optionName, InputDefinition input)
    {
        return input.PipelineType switch
        {
            "string" => JsonValue.Create(input.Default),
            "int" when int.TryParse(input.Default, out var intValue) => JsonValue.Create(intValue),
            "bool" when bool.TryParse(input.Default, out var boolValue) => JsonValue.Create(boolValue),
            "int" => throw new InvalidDataException(
                $"option {optionName} input {input.Name} 默认值不是合法 int。 "),
            "bool" => throw new InvalidDataException(
                $"option {optionName} input {input.Name} 默认值不是合法 bool。 "),
            _ => throw new InvalidDataException($"不支持 pipeline_type：{input.PipelineType}。 ")
        };
    }

    private static void MergeTemplated(
        JsonObject target,
        JsonElement template,
        IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        var node = JsonNode.Parse(template.GetRawText())!.AsObject();
        Substitute(node, substitutions);
        MergeObject(target, node);
    }

    private static void Substitute(
        JsonNode? node,
        IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    obj[property.Key] = SubstituteString(text, substitutions);
                }
                else
                {
                    Substitute(property.Value, substitutions);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    array[index] = SubstituteString(text, substitutions);
                }
                else
                {
                    Substitute(array[index], substitutions);
                }
            }
        }
    }

    private static JsonNode? SubstituteString(
        string text,
        IReadOnlyDictionary<string, JsonNode?> substitutions)
    {
        if (text.Length > 2 && text[0] == '{' && text[^1] == '}')
        {
            var name = text[1..^1];
            if (substitutions.TryGetValue(name, out var exact))
            {
                return exact?.DeepClone();
            }
        }

        var result = text;
        foreach (var (name, value) in substitutions)
        {
            var replacement = value switch
            {
                JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
                null => string.Empty,
                _ => value.ToJsonString(ProtocolJson.Options)
            };
            result = result.Replace($"{{{name}}}", replacement, StringComparison.Ordinal);
        }
        return JsonValue.Create(result);
    }

    private static void MergeObject(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonObject sourceObject
                && target[property.Key] is JsonObject targetObject)
            {
                MergeObject(targetObject, sourceObject);
            }
            else
            {
                target[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(ProtocolJson.Options));
        return document.RootElement.Clone();
    }
}
