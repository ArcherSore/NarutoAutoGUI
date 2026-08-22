using System.Text.Json;
using System.Text.RegularExpressions;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.ProjectModel;

internal static class ProjectInterfaceLoader
{
    private static readonly HashSet<string> AllowedTopLevelProperties = new(StringComparer.Ordinal)
    {
        "interface_version", "name", "label", "version", "description", "icon",
        "controller", "resource", "agent", "global_option", "task", "option"
    };

    private static readonly HashSet<string> AllowedControllerProperties = new(StringComparer.Ordinal)
    {
        "name", "label", "description", "icon", "type", "win32", "option"
    };

    private static readonly HashSet<string> AllowedWin32Properties = new(StringComparer.Ordinal)
    {
        "class_regex", "window_regex", "screencap", "mouse", "keyboard"
    };

    private static readonly HashSet<string> AllowedResourceProperties = new(StringComparer.Ordinal)
    {
        "name", "label", "description", "icon", "path", "option"
    };

    private static readonly HashSet<string> AllowedAgentProperties = new(StringComparer.Ordinal)
    {
        "child_exec", "child_args", "label", "description", "icon"
    };

    private static readonly HashSet<string> AllowedTaskProperties = new(StringComparer.Ordinal)
    {
        "name", "label", "description", "icon", "entry", "option", "pipeline_override"
    };

    private static readonly HashSet<string> AllowedOptionProperties = new(StringComparer.Ordinal)
    {
        "type", "label", "description", "icon", "default_case", "cases", "inputs", "pipeline_override"
    };

    private static readonly HashSet<string> AllowedInputProperties = new(StringComparer.Ordinal)
    {
        "name", "label", "description", "icon", "default", "pipeline_type", "verify", "pattern_msg"
    };

    private static readonly HashSet<string> AllowedCaseProperties = new(StringComparer.Ordinal)
    {
        "name", "label", "description", "icon", "option", "pipeline_override"
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
        RejectUnknownProperties(root, AllowedTopLevelProperties, "$");
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
        ValidateOptionDefinitions(options);
        ValidateOptionGraph(options);

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
        RejectUnknownProperties(obj, AllowedControllerProperties, "$.controller[0]");
        if (RequireString(obj, "type", "$.controller[0]") != "Win32")
        {
            throw new InvalidDataException("首版 controller.type 必须为 Win32。 ");
        }
        var win32 = RequireObject(RequireProperty(obj, "win32", "$.controller[0]"), "$.controller[0].win32");
        RejectUnknownProperties(win32, AllowedWin32Properties, "$.controller[0].win32");
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
            RejectUnknownProperties(obj, AllowedResourceProperties, path);
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
        RejectUnknownProperties(obj, AllowedAgentProperties, "$.agent");
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
            RejectUnknownProperties(obj, AllowedTaskProperties, path);
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
            RejectUnknownProperties(option, AllowedOptionProperties, path);
            var type = OptionalString(option, "type") ?? "select";
            if (type is not ("select" or "switch" or "input"))
            {
                throw new InvalidDataException($"首版不支持 option type：{type}（{path}）。 ");
            }
            result.Add(property.Name, new OptionDefinition(
                property.Name,
                ReadDisplayString(option, "label") ?? property.Name,
                ReadDisplayString(option, "description") ?? string.Empty,
                type,
                OptionalString(option, "default_case"),
                ParseInputs(option, path),
                ParseCases(option, path),
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
            RejectUnknownProperties(input, AllowedInputProperties, inputPath);
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
                ReadDisplayString(input, "label") ?? name,
                ReadDisplayString(input, "description") ?? string.Empty,
                defaultValue.GetString()!,
                pipelineType,
                OptionalString(input, "verify"),
                ReadDisplayString(input, "pattern_msg")));
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
            RejectUnknownProperties(item, AllowedCaseProperties, casePath);
            var name = RequireString(item, "name", casePath);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"重复 case.name：{name}（{casePath}）。 ");
            }
            result.Add(new CaseDefinition(
                name,
                ReadDisplayString(item, "label") ?? name,
                ReadDisplayString(item, "description") ?? string.Empty,
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
        ValidateReferences(global, "$.global_option", options);
        ValidateReferences(resource, "$.resource[0].option", options);
        ValidateReferences(controller, "$.controller[0].option", options);
        for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
        {
            ValidateReferences(tasks[taskIndex].Options, $"$.task[{taskIndex}].option", options);
        }
        foreach (var option in options.Values)
        {
            for (var caseIndex = 0; caseIndex < option.Cases.Count; caseIndex++)
            {
                ValidateReferences(
                    option.Cases[caseIndex].Options,
                    $"$.option.{option.Name}.cases[{caseIndex}].option",
                    options);
            }
        }
    }

    private static void ValidateReferences(
        IReadOnlyList<string> references,
        string path,
        IReadOnlyDictionary<string, OptionDefinition> options)
    {
        for (var index = 0; index < references.Count; index++)
        {
            if (!options.ContainsKey(references[index]))
            {
                throw new InvalidDataException(
                    $"{path}[{index}] 引用了不存在的 option：{references[index]}。 ");
            }
        }
    }

    private static void ValidateOptionDefinitions(
        IReadOnlyDictionary<string, OptionDefinition> options)
    {
        foreach (var option in options.Values)
        {
            var path = $"$.option.{option.Name}";
            if (option.Type == "input")
            {
                if (option.Inputs.Count == 0)
                {
                    throw new InvalidDataException($"{path}.inputs 必须至少包含一个 input。 ");
                }
                if (option.Cases.Count != 0)
                {
                    throw new InvalidDataException($"input option {path} 不能声明 cases。 ");
                }
                if (option.DefaultCase is not null)
                {
                    throw new InvalidDataException($"input option {path} 不能声明 default_case。 ");
                }
                for (var inputIndex = 0; inputIndex < option.Inputs.Count; inputIndex++)
                {
                    ValidateInputDefault(option.Inputs[inputIndex], $"{path}.inputs[{inputIndex}]");
                }
                continue;
            }

            if (option.Inputs.Count != 0)
            {
                throw new InvalidDataException($"{option.Type} option {path} 不能声明 inputs。 ");
            }
            if (option.Cases.Count == 0)
            {
                throw new InvalidDataException($"{path}.cases 必须至少包含一个 case。 ");
            }
            if (option.Type == "switch" && option.Cases.Count != 2)
            {
                throw new InvalidDataException($"switch option {path} 必须恰好有两个 case。 ");
            }
            if (option.DefaultCase is null)
            {
                throw new InvalidDataException($"{path}.default_case 必须是非空 string。 ");
            }
            if (!option.Cases.Any(item => item.Name == option.DefaultCase))
            {
                throw new InvalidDataException(
                    $"{path}.default_case 引用了不存在的 case：{option.DefaultCase}。 ");
            }
        }
    }

    private static void ValidateInputDefault(InputDefinition input, string path)
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
                throw new InvalidDataException($"{path}.verify 不是合法正则表达式。", exception);
            }

            try
            {
                if (!regex.IsMatch(input.Default))
                {
                    throw new InvalidDataException($"{path}.default 未通过 verify。 ");
                }
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new InvalidDataException($"{path}.default 的 verify 执行超时。", exception);
            }
        }

        if (input.PipelineType == "int" && !int.TryParse(input.Default, out _))
        {
            throw new InvalidDataException($"{path}.default 不是合法 int。 ");
        }
        if (input.PipelineType == "bool" && !bool.TryParse(input.Default, out _))
        {
            throw new InvalidDataException($"{path}.default 不是合法 bool。 ");
        }
    }

    private static void ValidateOptionGraph(
        IReadOnlyDictionary<string, OptionDefinition> options)
    {
        var validated = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        foreach (var optionName in options.Keys)
        {
            ValidateOptionGraph(optionName, options, validated, visiting, path);
        }
    }

    private static void ValidateOptionGraph(
        string optionName,
        IReadOnlyDictionary<string, OptionDefinition> options,
        HashSet<string> validated,
        HashSet<string> visiting,
        List<string> path)
    {
        if (validated.Contains(optionName))
        {
            return;
        }
        if (!visiting.Add(optionName))
        {
            var cycleStart = path.IndexOf(optionName);
            var cycle = path.Skip(cycleStart).Append(optionName);
            throw new InvalidDataException($"option 递归引用形成循环：{string.Join(" -> ", cycle)}。 ");
        }

        path.Add(optionName);
        foreach (var nested in options[optionName].Cases.SelectMany(item => item.Options))
        {
            ValidateOptionGraph(nested, options, validated, visiting, path);
        }
        path.RemoveAt(path.Count - 1);
        visiting.Remove(optionName);
        validated.Add(optionName);
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

    private static string? ReadDisplayString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{name} 必须是 string。 ");
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
        IReadOnlySet<string> allowed,
        string path)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"首版不支持执行字段 {path}.{property.Name}。 ");
            }
        }
    }
}
