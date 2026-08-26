namespace NarutoAutoGUI.ChildSession;

internal enum ChildSessionState
{
    NotRunning,
    Existing,
    Connecting,
    ConnectedVisible,
    ConnectedHidden,
    Disconnecting,
    Faulted
}

internal sealed record ChildSessionSnapshot(
    ChildSessionState State, uint? ChildSessionId, int RdpConnectedState, string Detail)
{
    internal static ChildSessionSnapshot Empty { get; } =
        new(ChildSessionState.NotRunning, null, 0, "未运行");
}
