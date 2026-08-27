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

    private static readonly HashSet<string> AllowedWin32ScreencapMethods = new(StringComparer.Ordinal)
    {
        "None", "GDI", "FramePool", "DXGI_DesktopDup", "DXGI_DesktopDup_Window",
        "PrintWindow", "ScreenDC"
    };

    private static readonly HashSet<string> AllowedWin32InputMethods = new(StringComparer.Ordinal)
    {
        "None", "Seize", "SendMessage", "PostMessage", "LegacyEvent", "PostThreadMessage",
        "SendMessageWithCursorPos", "PostMessageWithCursorPos", "SendMessageWithWindowPos",
        "PostMessageWithWindowPos"
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
        if (!File.Exists(interfacePath)) {
            throw new FileNotFoundException(
                "安装目录缺少 interface.json，请确认使用完整的 MaaNOP 发布包。", interfacePath);
        }

        var bytes = File.ReadAllBytes(interfacePath);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var root = RequireObject(document.RootElement, "$");
        RejectUnknownProperties(root, AllowedTopLevelProperties, "$");
        var interfaceVersion = RequireInt(root, "interface_version", "$");
        if (interfaceVersion != 2) {
            throw new InvalidDataException($"只支持 interface_version=2，实际为 {interfaceVersion}。 ");
        }

        var controllers = RequireArray(root, "controller", "$").EnumerateArray().ToArray();
        if (controllers.Length != 1) {
            throw new InvalidDataException("首版要求恰好一个 Win32 controller。 ");
        }
        var controller = ParseController(controllers[0]);

        var resourceElements = RequireArray(root, "resource", "$").EnumerateArray().ToArray();
        if (resourceElements.Length != 1) {
            throw new InvalidDataException("首版要求恰好一个 resource。 ");
        }
        var resources = ParseResources(resourceElements, projectRoot);
        var (agentExec, agentArgs) = ParseAgent(RequireProperty(root, "agent", "$"));
        var agent = new AgentDefinition(agentExec, agentArgs, projectRoot);
        var tasks = ParseTasks(RequireArray(root, "task", "$"));
        var options = ParseOptions(RequireProperty(root, "option", "$"));
        var globalOptions = ReadStringArray(root, "global_option", required: false, "$");
        ValidateReferences(globalOptions, tasks, options);
        ValidateOptionDefinitions(options);
        ValidateOptionGraph(options);

        var provenance = new ProjectProvenance(
            RequireString(root, "name", "$"),
            OptionalString(root, "version") ?? string.Empty, interfaceVersion,
            CanonicalDigest.ComputeSourceInterfaceDigest(bytes));
        var runtimeProfileDigest = CanonicalDigest.ComputeRuntimeProfileDigestV1(
            projectRoot, controller, resources, agent);
        return new ProjectDefinition(
            projectRoot, provenance, controller, resources, agent, runtimeProfileDigest,
            globalOptions, tasks, options);
    }

    private static Win32ControllerDefinition ParseController(JsonElement element)
    {
        var obj = RequireObject(element, "$.controller[0]");
        RejectUnknownProperties(obj, AllowedControllerProperties, "$.controller[0]");
        if (RequireString(obj, "type", "$.controller[0]") != "Win32") {
            throw new InvalidDataException("首版 controller.type 必须为 Win32。 ");
        }
        var win32 = RequireObject(RequireProperty(obj, "win32", "$.controller[0]"), "$.controller[0].win32");
        RejectUnknownProperties(win32, AllowedWin32Properties, "$.controller[0].win32");
        var classRegex = RequireString(win32, "class_regex", "$.controller[0].win32");
        var windowRegex = RequireString(win32, "window_regex", "$.controller[0].win32");
        ValidateRegex(classRegex, "$.controller[0].win32.class_regex");
        ValidateRegex(windowRegex, "$.controller[0].win32.window_regex");
        RejectNonEmptyOptionScope(obj, "$.controller[0]");
        return new Win32ControllerDefinition(
            RequireString(obj, "name", "$.controller[0]"),
            classRegex, windowRegex,
            RequireSupportedString(win32, "screencap", "$.controller[0].win32", AllowedWin32ScreencapMethods),
            RequireSupportedString(win32, "mouse", "$.controller[0].win32", AllowedWin32InputMethods),
            RequireSupportedString(win32, "keyboard", "$.controller[0].win32", AllowedWin32InputMethods));
    }

    private static IReadOnlyList<ResourceDefinition> ParseResources(IReadOnlyList<JsonElement> elements, string projectRoot)
    {
        var resources = new List<ResourceDefinition>();
        for (var index = 0; index < elements.Count; index++) {
            var path = $"$.resource[{index}]";
            var obj = RequireObject(elements[index], path);
            RejectUnknownProperties(obj, AllowedResourceProperties, path);
            var resourcePaths = ReadStringArray(obj, "path", required: true, path)
                .Select(value => PathCanonicalizerV1.Canonicalize(
                    Path.IsPathFullyQualified(value) ? value : Path.Combine(projectRoot, value)))
                .ToArray();
            RejectNonEmptyOptionScope(obj, path);
            resources.Add(new ResourceDefinition(RequireString(obj, "name", path), resourcePaths));
        }
        return resources;
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
        foreach (var element in array.EnumerateArray()) {
            var path = $"$.task[{index++}]";
            var obj = RequireObject(element, path);
            RejectUnknownProperties(obj, AllowedTaskProperties, path);
            var name = RequireString(obj, "name", path);
            if (!names.Add(name)) {
                throw new InvalidDataException($"重复 task.name：{name}。 ");
            }
            tasks.Add(new TaskDefinition(
                name, OptionalString(obj, "label") ?? name,
                ReadOptionalDescription(obj, "description") ?? string.Empty,
                RequireString(obj, "entry", path),
                ReadStringArray(obj, "option", required: false, path),
                ReadObjectOrEmpty(obj, "pipeline_override", path)));
        }
        if (tasks.Count == 0) {
            throw new InvalidDataException("PI 至少需要一个 task。 ");
        }
        return tasks;
    }

    private static IReadOnlyDictionary<string, OptionDefinition> ParseOptions(JsonElement element)
    {
        var obj = RequireObject(element, "$.option");
        var result = new Dictionary<string, OptionDefinition>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject()) {
            var path = $"$.option.{property.Name}";
            var option = RequireObject(property.Value, path);
            RejectUnknownProperties(option, AllowedOptionProperties, path);
            var kind = ParseOptionKind(OptionalString(option, "type") ?? "select", path);
            if (kind is OptionDefinitionKind.Select or OptionDefinitionKind.Switch
                && option.TryGetProperty("pipeline_override", out _)) {
                throw new InvalidDataException(
                    $"{FormatOptionKind(kind)} option {path} 不能声明 pipeline_override；请放到对应 case 中。 ");
            }
            result.Add(property.Name, new OptionDefinition(
                property.Name,
                ReadDisplayString(option, "label") ?? property.Name,
                ReadDisplayString(option, "description") ?? string.Empty,
                kind, OptionalString(option, "default_case"),
                ParseInputs(option, path), ParseCases(option, path),
                ReadObjectOrEmpty(option, "pipeline_override", path)));
        }
        return result;
    }

    private static IReadOnlyList<InputDefinition> ParseInputs(JsonElement option, string path)
    {
        if (!option.TryGetProperty("inputs", out var array)) {
            return [];
        }
        RequireArrayValue(array, $"{path}.inputs");
        var result = new List<InputDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray()) {
            var inputPath = $"{path}.inputs[{index++}]";
            var input = RequireObject(element, inputPath);
            RejectUnknownProperties(input, AllowedInputProperties, inputPath);
            var name = RequireString(input, "name", inputPath);
            if (!names.Add(name)) {
                throw new InvalidDataException($"重复 input name：{name}（{inputPath}）。 ");
            }
            if (!input.TryGetProperty("default", out var defaultValue)
                || defaultValue.ValueKind != JsonValueKind.String) {
                throw new InvalidDataException($"default-only slice 要求 {inputPath}.default 为 string。 ");
            }
            var pipelineKind = ParsePipelineKind(OptionalString(input, "pipeline_type") ?? "string", inputPath);
            result.Add(new InputDefinition(
                name, ReadDisplayString(input, "label") ?? name,
                ReadDisplayString(input, "description") ?? string.Empty, defaultValue.GetString()!,
                pipelineKind, OptionalString(input, "verify"), ReadDisplayString(input, "pattern_msg")));
        }
        return result;
    }

    private static IReadOnlyList<CaseDefinition> ParseCases(JsonElement option, string path)
    {
        if (!option.TryGetProperty("cases", out var array)) {
            return [];
        }
        RequireArrayValue(array, $"{path}.cases");
        var result = new List<CaseDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray()) {
            var casePath = $"{path}.cases[{index++}]";
            var item = RequireObject(element, casePath);
            RejectUnknownProperties(item, AllowedCaseProperties, casePath);
            var name = RequireString(item, "name", casePath);
            if (!names.Add(name)) {
                throw new InvalidDataException($"重复 case.name：{name}（{casePath}）。 ");
            }
            result.Add(new CaseDefinition(
                name, ReadDisplayString(item, "label") ?? name,
                ReadDisplayString(item, "description") ?? string.Empty,
                ReadStringArray(item, "option", required: false, casePath),
                ReadObjectOrEmpty(item, "pipeline_override", casePath)));
        }
        return result;
    }

    private static void ValidateReferences(
        IReadOnlyList<string> global, IReadOnlyList<TaskDefinition> tasks,
        IReadOnlyDictionary<string, OptionDefinition> options)
    {
        ValidateReferences(global, "$.global_option", options);
        for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++) {
            ValidateReferences(tasks[taskIndex].Options, $"$.task[{taskIndex}].option", options);
        }
        foreach (var option in options.Values) {
            for (var caseIndex = 0; caseIndex < option.Cases.Count; caseIndex++) {
                ValidateReferences(
                    option.Cases[caseIndex].Options, $"$.option.{option.Name}.cases[{caseIndex}].option",
                    options);
            }
        }
    }

    private static void ValidateReferences(
        IReadOnlyList<string> references, string path,
        IReadOnlyDictionary<string, OptionDefinition> options)
    {
        for (var index = 0; index < references.Count; index++) {
            if (!options.ContainsKey(references[index])) {
                throw new InvalidDataException(
                    $"{path}[{index}] 引用了不存在的 option：{references[index]}。 ");
            }
        }
    }

    private static void ValidateOptionDefinitions(IReadOnlyDictionary<string, OptionDefinition> options)
    {
        foreach (var option in options.Values) {
            var path = $"$.option.{option.Name}";
            if (option.Kind == OptionDefinitionKind.Input) {
                if (option.Inputs.Count == 0) {
                    throw new InvalidDataException($"{path}.inputs 必须至少包含一个 input。 ");
                }
                if (option.Cases.Count != 0) {
                    throw new InvalidDataException($"input option {path} 不能声明 cases。 ");
                }
                if (option.DefaultCase is not null) {
                    throw new InvalidDataException($"input option {path} 不能声明 default_case。 ");
                }
                for (var inputIndex = 0; inputIndex < option.Inputs.Count; inputIndex++) {
                    var input = option.Inputs[inputIndex];
                    _ = ProjectInputValue.Parse(
                        input, input.Default, $"{path}.inputs[{inputIndex}].default");
                }
                continue;
            }

            if (option.Inputs.Count != 0) {
                throw new InvalidDataException(
                    $"{FormatOptionKind(option.Kind)} option {path} 不能声明 inputs。 ");
            }
            if (option.Cases.Count == 0) {
                throw new InvalidDataException($"{path}.cases 必须至少包含一个 case。 ");
            }
            if (option.Kind == OptionDefinitionKind.Switch && option.Cases.Count != 2) {
                throw new InvalidDataException($"switch option {path} 必须恰好有两个 case。 ");
            }
            if (option.DefaultCase is null) {
                throw new InvalidDataException($"{path}.default_case 必须是非空 string。 ");
            }
            if (!option.Cases.Any(item => item.Name == option.DefaultCase)) {
                throw new InvalidDataException(
                    $"{path}.default_case 引用了不存在的 case：{option.DefaultCase}。 ");
            }
        }
    }

    private static OptionDefinitionKind ParseOptionKind(string value, string path) =>
        value switch {
            "select" => OptionDefinitionKind.Select,
            "switch" => OptionDefinitionKind.Switch,
            "input" => OptionDefinitionKind.Input,
            _ => throw new InvalidDataException(
                $"首版不支持 option type：{value}（{path}）。 ")
        };

    private static PipelineValueKind ParsePipelineKind(string value, string path) =>
        value switch {
            "string" => PipelineValueKind.String,
            "int" => PipelineValueKind.Int,
            "bool" => PipelineValueKind.Bool,
            _ => throw new InvalidDataException(
                $"不支持 pipeline_type={value}（{path}）。 ")
        };

    private static string FormatOptionKind(OptionDefinitionKind kind) =>
        kind.ToString().ToLowerInvariant();

    private static void ValidateOptionGraph(IReadOnlyDictionary<string, OptionDefinition> options)
    {
        var validated = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        foreach (var optionName in options.Keys) {
            ValidateOptionGraph(optionName, options, validated, visiting, path);
        }
    }

    private static void ValidateOptionGraph(
        string optionName, IReadOnlyDictionary<string, OptionDefinition> options,
        HashSet<string> validated, HashSet<string> visiting, List<string> path)
    {
        if (validated.Contains(optionName)) {
            return;
        }
        if (!visiting.Add(optionName)) {
            var cycleStart = path.IndexOf(optionName);
            var cycle = path.Skip(cycleStart).Append(optionName);
            throw new InvalidDataException($"option 递归引用形成循环：{string.Join(" -> ", cycle)}。 ");
        }

        path.Add(optionName);
        foreach (var nested in options[optionName].Cases.SelectMany(item => item.Options)) {
            ValidateOptionGraph(nested, options, validated, visiting, path);
        }
        path.RemoveAt(path.Count - 1);
        visiting.Remove(optionName);
        validated.Add(optionName);
    }

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) {
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
        if (value.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException($"{path} 必须是 array。 ");
        }
    }

    private static JsonElement RequireProperty(JsonElement obj, string name, string path)
    {
        if (!obj.TryGetProperty(name, out var value)) {
            throw new InvalidDataException($"缺少 {path}.{name}。 ");
        }
        return value;
    }

    private static string RequireString(JsonElement obj, string name, string path) =>
        OptionalString(obj, name)
        ?? throw new InvalidDataException($"{path}.{name} 必须是非空 string。 ");

    private static void RejectNonEmptyOptionScope(JsonElement obj, string path)
    {
        var options = ReadStringArray(obj, "option", required: false, path);
        if (options.Count != 0) {
            throw new InvalidDataException(
                $"当前 MaaNOP GUI 不支持 {path}.option；该数组必须省略或为空。 ");
        }
    }

    private static string RequireSupportedString(JsonElement obj, string name, string path, IReadOnlySet<string> supported)
    {
        var value = RequireString(obj, name, path);
        if (!supported.Contains(value)) {
            throw new InvalidDataException(
                $"{path}.{name} 不支持值 {value}；可选值：{string.Join(", ", supported)}。 ");
        }
        return value;
    }

    private static void ValidateRegex(string pattern, string path)
    {
        try {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        } catch (ArgumentException exception) {
            throw new InvalidDataException($"{path} 不是合法正则表达式。", exception);
        }
    }

    private static string? OptionalString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) {
            throw new InvalidDataException($"{name} 必须是非空 string。 ");
        }
        return value.GetString();
    }

    private static string? ReadDisplayString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"{name} 必须是 string。 ");
        }
        return value.GetString();
    }

    private static string? ReadOptionalDescription(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"{name} 必须是 string 或 null。 ");
        }
        return value.GetString();
    }

    private static int RequireInt(JsonElement obj, string name, string path)
    {
        var value = RequireProperty(obj, name, path);
        if (!value.TryGetInt32(out var result)) {
            throw new InvalidDataException($"{path}.{name} 必须是 int。 ");
        }
        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string name, bool required, string path)
    {
        if (!obj.TryGetProperty(name, out var value)) {
            return required
                ? throw new InvalidDataException($"缺少 {path}.{name}。 ")
                : [];
        }
        RequireArrayValue(value, $"{path}.{name}");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) {
                throw new InvalidDataException($"{path}.{name} 只能包含非空 string。 ");
            }
            result.Add(item.GetString()!);
        }
        return result;
    }

    private static JsonElement ReadObjectOrEmpty(JsonElement obj, string name, string path)
    {
        if (!obj.TryGetProperty(name, out var value)) {
            return ProtocolJson.ToElement(new { });
        }
        if (value.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException($"{path}.{name} 必须是 object。 ");
        }
        return value.Clone();
    }

    private static void RejectUnknownProperties(JsonElement obj, IReadOnlySet<string> allowed, string path)
    {
        foreach (var property in obj.EnumerateObject()) {
            if (!allowed.Contains(property.Name)) {
                throw new InvalidDataException($"首版不支持执行字段 {path}.{property.Name}。 ");
            }
        }
    }
}
