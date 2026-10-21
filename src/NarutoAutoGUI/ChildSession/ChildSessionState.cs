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

// InputUnlocked is true only while the viewer is shown and lets mouse and keyboard reach the Child Session.
internal sealed record ChildSessionSnapshot(
    ChildSessionState State, uint? ChildSessionId, int RdpConnectedState, string Detail, bool InputUnlocked = false)
{
    internal static ChildSessionSnapshot Empty { get; } =
        new(ChildSessionState.NotRunning, null, 0, "未运行");
}
