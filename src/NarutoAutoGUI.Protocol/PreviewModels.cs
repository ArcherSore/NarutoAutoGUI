namespace NarutoAutoGUI.Protocol;

public enum PreviewState { Preparing, WaitingForWindow, WaitingForFrame, Streaming, Retiring, Unavailable }

public sealed record PreviewIdentity(Guid WorkerInstanceId, uint ChildSessionId, Guid SubscriptionId);
public sealed record PreviewRequest(Guid WorkerInstanceId, Guid SubscriptionId);
public sealed record PreviewDescriptor(
    PreviewIdentity Identity, int OwnerPid, long OwnerStartedAtUtc, int FormatVersion);
public sealed record PreviewResponse(
    PreviewIdentity Identity, PreviewState State, long Generation, PreviewDescriptor? Descriptor);
public sealed record PreviewFrameInfo(
    PreviewState State, long Generation, long Revision, DateTime SampledAtUtc, int Width, int Height);
