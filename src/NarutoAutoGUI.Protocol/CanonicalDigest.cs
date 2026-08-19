using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NarutoAutoGUI.Protocol;

public static class CanonicalDigest
{
    private const string RuntimeProfileDomain = "NarutoAutoGUI.RuntimeProfileDigest.v1\n";
    private const string RunPlanDomain = "NarutoAutoGUI.RunPlanDigest.v1\n";

    public static string ComputeSourceInterfaceDigest(ReadOnlySpan<byte> bytes) =>
        FormatDigest(SHA256.HashData(bytes));

    public static string ComputeRuntimeProfileDigestV1(
        string projectRoot,
        Win32ControllerDefinition controller,
        IReadOnlyList<ResourceDefinition> resources,
        AgentDefinition agent)
    {
        var normalizedProjectRoot = PathCanonicalizerV1.Canonicalize(projectRoot);
        var normalizedResources = resources.Select(resource => new ResourceDefinition(
            resource.Name,
            resource.Paths.Select(PathCanonicalizerV1.Canonicalize).ToArray())).ToArray();
        var normalizedAgent = agent with
        {
            WorkingDirectory = PathCanonicalizerV1.Canonicalize(agent.WorkingDirectory)
        };

        var element = ProtocolJson.ToElement(new
        {
            projectRoot = normalizedProjectRoot,
            controller = new
            {
                type = "Win32",
                controller.ClassRegex,
                controller.WindowRegex,
                controller.ScreencapMethod,
                controller.MouseMethod,
                controller.KeyboardMethod
            },
            resources = normalizedResources.Select(resource => new
            {
                resource.Name,
                paths = resource.Paths
            }),
            agent = new
            {
                normalizedAgent.ChildExec,
                childArgs = normalizedAgent.ChildArgs,
                normalizedAgent.WorkingDirectory
            }
        });

        return ComputeWithDomain(RuntimeProfileDomain, element);
    }

    public static string ComputePlanDigestV1(RunPlan plan)
    {
        var createdAtUtc = plan.CreatedAtUtc.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);
        var element = ProtocolJson.ToElement(new
        {
            planVersion = plan.PlanVersion,
            createdAtUtc,
            project = plan.Project,
            runtimeProfileDigest = plan.RuntimeProfileDigest,
            resolvedGlobalOptions = plan.ResolvedGlobalOptions,
            items = plan.Items.Select(item => new
            {
                planItemId = item.PlanItemId,
                item.TaskName,
                item.TaskLabel,
                item.Entry,
                resolvedOptions = item.ResolvedOptions,
                pipelineOverride = item.PipelineOverride
            })
        });

        return ComputeWithDomain(RunPlanDomain, element);
    }

    public static byte[] GetCanonicalUtf8(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false
               }))
        {
            WriteCanonical(writer, element);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static void ValidateDigestFormat(string digest, string parameterName)
    {
        if (digest.Length != 71
            || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            throw new ArgumentException(
                "摘要必须使用 sha256:<64 lowercase hex> 格式。",
                parameterName);
        }
    }

    private static string ComputeWithDomain(string domain, JsonElement element)
    {
        var canonical = GetCanonicalUtf8(element);
        var domainBytes = Encoding.UTF8.GetBytes(domain);
        var combined = new byte[domainBytes.Length + canonical.Length];
        domainBytes.CopyTo(combined, 0);
        canonical.CopyTo(combined, domainBytes.Length);
        return FormatDigest(SHA256.HashData(combined));
    }

    private static string FormatDigest(ReadOnlySpan<byte> digest) =>
        "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Canonical JSON 不支持 {element.ValueKind}。 ");
        }
    }
}

public static class PathCanonicalizerV1
{
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("参与 Runtime Profile Digest 的路径必须是绝对路径。", nameof(path));
        }

        var full = Path.GetFullPath(path).Replace('/', '\\');
        if (full.Length >= 2 && full[1] == ':' && char.IsLetter(full[0]))
        {
            full = char.ToUpperInvariant(full[0]) + full[1..];
        }

        var root = Path.GetPathRoot(full)?.Replace('/', '\\') ?? string.Empty;
        while (full.Length > root.Length && full.EndsWith('\\'))
        {
            full = full[..^1];
        }

        return full;
    }
}
