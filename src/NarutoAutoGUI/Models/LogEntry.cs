namespace NarutoAutoGUI.Models;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Critical = 4
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public string DisplayLevel => Level.ToString().ToUpperInvariant();

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] [{DisplayLevel}] {Message}";
}
