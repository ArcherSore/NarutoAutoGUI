using System.Text.Json;
using System.Text.Json.Serialization;

namespace NarutoAutoGUI.Protocol;

public static class ProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const int SnapshotVersion = 1;
    public const int LaunchContextVersion = 1;
    public const int PlanVersion = 1;
    public const int MaximumFramePayloadBytes = 4 * 1024 * 1024;
    public const int MaximumLaunchManifestBytes = 256 * 1024;
    public const int MaximumRunPlanBytes = 1024 * 1024;
    public const int MaximumSnapshotPayloadBytes = 3 * 1024 * 1024;
    public const int MaximumLogMessageBytes = 64 * 1024;
    public const int MaximumLogGetSinceResponseBytes = 1024 * 1024;
    public const int PreviewIntervalMilliseconds = 200;
    public const int MaximumPreviewPixelWidth = 640;
    public const int MaximumPreviewPixelHeight = 360;
    public const int MaximumPreviewPngBytes = 1400 * 1024;
    public const int MaximumPreviewResponseBytes = 2 * 1024 * 1024;
    public const string MaaNopRunLogSource = "maanop.run";
}

public static class ProtocolOperations
{
    public const string ConnectionOpen = "connection.open";
    public const string WorkerGetSnapshot = "worker.getSnapshot";
    public const string RunStart = "run.start";
    public const string RunStop = "run.stop";
    public const string LogGetSince = "log.getSince";
    public const string PreviewGetLatest = "preview.getLatest";
    public const string WorkerStateChanged = "worker.stateChanged";
    public const string RunStateChanged = "run.stateChanged";
    public const string LogEntry = "log.entry";
}

public static class ProtocolMessageTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(Options)
        ?? throw new JsonException($"无法反序列化 {typeof(T).Name}。 ");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record WireEnvelope
{
    public required int ProtocolVersion { get; init; }
    public required string MessageType { get; init; }
    public required string Operation { get; init; }
    public Guid? RequestId { get; init; }
    public bool? Success { get; init; }
    public ProtocolError? Error { get; init; }
    public required JsonElement Data { get; init; }

    public static WireEnvelope Request<T>(string operation, Guid requestId, T data) => new() {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageType = ProtocolMessageTypes.Request,
        Operation = operation,
        RequestId = requestId,
        Data = ProtocolJson.ToElement(data)
    };

    public static WireEnvelope Response<T>(string operation, Guid requestId, T data) => new() {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageType = ProtocolMessageTypes.Response,
        Operation = operation,
        RequestId = requestId,
        Success = true,
        Data = ProtocolJson.ToElement(data)
    };

    public static WireEnvelope Failure(
        string operation, Guid requestId, string code, string message,
        bool retriable = false, JsonElement? details = null) => new() {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            MessageType = ProtocolMessageTypes.Response,
            Operation = operation,
            RequestId = requestId,
            Success = false,
            Error = new ProtocolError(code, message, retriable, details),
            Data = ProtocolJson.ToElement(new { })
        };

    public static WireEnvelope Event<T>(string operation, T data) => new() {
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        MessageType = ProtocolMessageTypes.Event,
        Operation = operation,
        Data = ProtocolJson.ToElement(data)
    };
}

public sealed record ProtocolError(string Code, string Message, bool Retriable, JsonElement? Details = null);

public sealed record ProjectProvenance(string Name, string Version, int InterfaceVersion, string SourceInterfaceDigest);

public sealed record Win32ControllerDefinition(
    string Name, string ClassRegex, string WindowRegex,
    string ScreencapMethod, string MouseMethod, string KeyboardMethod);

public sealed record ResourceDefinition(string Name, IReadOnlyList<string> Paths);

public sealed record AgentDefinition(string ChildExec, IReadOnlyList<string> ChildArgs, string WorkingDirectory);

public sealed record LaunchManifest(
    int LaunchContextVersion, Guid WorkerInstanceId, string RuntimeProfileDigest,
    string ProjectRoot, ProjectProvenance Project, Win32ControllerDefinition Controller,
    IReadOnlyList<ResourceDefinition> Resources, AgentDefinition Agent);

public sealed record RunPlan(
    int PlanVersion, DateTime CreatedAtUtc, ProjectProvenance Project,
    string RuntimeProfileDigest, JsonElement ResolvedGlobalOptions,
    IReadOnlyList<RunPlanItem> Items);

public sealed record RunPlanItem(
    Guid PlanItemId, string TaskName, string TaskLabel, string Entry,
    JsonElement ResolvedOptions, JsonElement PipelineOverride);

public enum WorkerState { Starting, Ready, NotReady, Faulted }

public enum RunState { Idle, Starting, Running, Stopping, Succeeded, Failed, Cancelled }

public enum PlanItemState { Pending, Starting, Running, Succeeded, Failed, Cancelled }

public sealed record StructuredReason(string Code, string Message, JsonElement? Details = null);

public sealed record DependencyCheck(bool Success, string? Value, string? Error);

public sealed record DependencyStatus(
    DateTime CheckedAtUtc, string MaaFrameworkBindingVersion, string MaaFrameworkRuntimeVersion,
    DependencyCheck Python, DependencyCheck MaaImport, DependencyCheck AgentServerImport,
    DependencyCheck ToolkitImport, DependencyCheck AgentEntry);

public sealed record WorkerSnapshot(
    int SnapshotVersion, DateTime CapturedAtUtc, long StateRevision, Guid WorkerInstanceId,
    int WorkerPid, uint ChildSessionId, string WorkerVersion, int ProtocolVersion,
    string RuntimeProfileDigest, ProjectProvenance Project, WorkerState WorkerState,
    StructuredReason? WorkerReason, DependencyStatus DependencyStatus, RunState RunState,
    RunSnapshot? ActiveRun, RunSnapshot? LastRun, long FirstAvailableLogSequence,
    long LastLogSequence);

public sealed record RunSnapshot(
    Guid RunId, string PlanDigest, RunState State, DateTime CreatedAtUtc,
    DateTime? StartedAtUtc, DateTime? StopRequestedAtUtc, DateTime? EndedAtUtc,
    Guid? CurrentPlanItemId, int? CurrentPlanItemIndex, RunPlan Plan,
    IReadOnlyList<PlanItemSnapshot> Items, JsonElement? Result, StructuredReason? Error);

public sealed record PlanItemSnapshot(
    Guid PlanItemId, string TaskName, string TaskLabel, string Entry,
    JsonElement ResolvedOptions, JsonElement PipelineOverride, PlanItemState State,
    DateTime? StartedAtUtc, DateTime? EndedAtUtc, string? Reason,
    JsonElement? Result, StructuredReason? Error);

public sealed record WorkerLogEntry(
    long Sequence, DateTime TimestampUtc, string Level, string Source, string Message,
    bool Truncated, int? OriginalByteLength, Guid? RunId, Guid? PlanItemId, string? TaskName);

public sealed record ConnectionOpenRequest(
    Guid WorkerInstanceId, string LaunchToken, string WorkerVersion, string RuntimeProfileDigest);

public sealed record ConnectionOpenResponse(bool Accepted, Guid WorkerInstanceId, int WorkerPid, uint ChildSessionId);

public sealed record GetSnapshotResponse(WorkerSnapshot Snapshot);

public sealed record RunStartRequest(Guid RunId, string PlanDigest, RunPlan Plan);

public sealed record RunStartResponse(string Disposition, RunSnapshot Run);

public sealed record RunStopRequest(Guid RunId);

public sealed record RunStopResponse(string Disposition, RunState State);

public sealed record LogGetSinceRequest(long AfterSequence, int Limit);

public sealed record LogGetSinceResponse(
    IReadOnlyList<WorkerLogEntry> Entries, int EffectiveLimit, long FirstAvailableSequence,
    long LastLogSequence, bool HasMore, bool Gap,
    long? MissingFromSequence, long? MissingToSequence);

public sealed record PreviewGetLatestRequest(Guid RunId, long AfterRevision);

public sealed record PreviewGetLatestResponse(
    string Disposition, Guid WorkerInstanceId, Guid? RunId, long Revision,
    DateTime? SampledAtUtc, int? PixelWidth, int? PixelHeight, string? ContentType,
    byte[]? PngBytes, string? Reason);

public sealed record StateChangedEvent(Guid WorkerInstanceId, long StateRevision, WorkerSnapshot Snapshot);

public sealed record LogEntryEvent(Guid WorkerInstanceId, WorkerLogEntry Entry);
