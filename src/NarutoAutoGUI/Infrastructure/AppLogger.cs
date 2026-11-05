using System.Diagnostics;
using System.Runtime.CompilerServices;
using NarutoAutoGUI.Models;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NarutoAutoGUI.Infrastructure;

// The application's logging surface over Serilog, which owns the files, rolling and retention. Callers pass rendered
// text, so a message's braces (JSON, for one) are never parsed as a Serilog template.
internal sealed class AppLogger : IDisposable
{
    private const long MaximumFileBytes = 10L * 1024L * 1024L;
    private const string OutputTemplate =
        "[{Timestamp:HH:mm:ss.fff}] [{LevelName:l}] [T{ThreadId}] [{Source:l}] {Message:l}{NewLine}{Exception}";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private readonly object _sync = new();
    private readonly Logger _logger;
    private bool _disposed;

    internal AppLogger(string? logDirectory = null)
    {
        string? fallbackMessage = null;
        if (logDirectory is null) {
            (logDirectory, fallbackMessage) = ResolveDefaultLogDirectory();
        }
        Directory.CreateDirectory(logDirectory);
        LogDirectory = logDirectory;
        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "NarutoAutoGUI-.log"), outputTemplate: OutputTemplate,
                rollingInterval: RollingInterval.Day, fileSizeLimitBytes: MaximumFileBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: null, retainedFileTimeLimit: Retention, shared: true)
            .CreateLogger();
        if (fallbackMessage is not null) {
            Warn(fallbackMessage);
        }
    }

    internal event EventHandler<LogEntry>? EntryWritten;

    internal string LogDirectory { get; }

    internal void Debug(
        string message, [CallerFilePath] string file = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) => Emit(LogLevel.Debug, FormatSource(file, member, line), message, null);

    internal void Info(
        string message, [CallerFilePath] string file = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) => Emit(LogLevel.Info, FormatSource(file, member, line), message, null);

    internal void Warn(
        string message, Exception? exception = null, [CallerFilePath] string file = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0) =>
        Emit(LogLevel.Warn, FormatSource(file, member, line), message, exception);

    internal void Error(
        string message, Exception? exception = null, [CallerFilePath] string file = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0) =>
        Emit(LogLevel.Error, FormatSource(file, member, line), message, exception);

    internal void Critical(
        string message, Exception? exception = null, [CallerFilePath] string file = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0) =>
        Emit(LogLevel.Critical, FormatSource(file, member, line), message, exception);

    // Worker logs are relayed here, so the local call site says nothing about where they came from.
    internal void WriteWorker(LogLevel level, string message) => Emit(level, "Worker", message, null);

    public void Dispose()
    {
        lock (_sync) {
            if (_disposed) {
                return;
            }

            _disposed = true;
            _logger.Dispose();
        }
    }

    private void Emit(LogLevel level, string source, string message, Exception? exception)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (_sync) {
            if (_disposed) {
                return;
            }

            try {
                _logger.ForContext("LevelName", entry.DisplayLevel)
                    .ForContext("ThreadId", Environment.CurrentManagedThreadId)
                    .ForContext("Source", source)
                    .Write(ToSerilogLevel(level), exception, "{Text:l}", message);
            } catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException) {
                Debugger.Log(0, "NarutoAutoGUI", $"File logging failed: {loggingException}\n");
            }
        }

        try {
            EntryWritten?.Invoke(this, entry);
        } catch (Exception subscriberException) {
            Debugger.Log(0, "NarutoAutoGUI", $"Log subscriber failed: {subscriberException}\n");
        }
    }

    // A partial class keeps its part name (MainWindow.Updates); ".xaml" only repeats the class.
    private static string FormatSource(string file, string member, int line)
    {
        var type = Path.GetFileNameWithoutExtension(file).Replace(".xaml", string.Empty, StringComparison.Ordinal);
        return $"{type}.{member}:{line}";
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch {
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Info => LogEventLevel.Information,
        LogLevel.Warn => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal
    };

    private static (string Path, string? Warning) ResolveDefaultLogDirectory()
    {
        var preferred = Path.Combine(AppContext.BaseDirectory, "logs");
        try {
            Directory.CreateDirectory(preferred);
            return (preferred, null);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(localData, "NarutoAutoGUI", "logs"),
                Path.Combine(Path.GetTempPath(), "NarutoAutoGUI", "logs")
            };

            foreach (var candidate in candidates) {
                try {
                    Directory.CreateDirectory(candidate);
                    return (candidate, $"程序目录日志不可写，已回退到：{candidate}。原因：{exception.Message}");
                } catch (Exception candidateException) when (candidateException is IOException
                                                                   or UnauthorizedAccessException) {
                    // Try the next user-writable location.
                }
            }

            throw new IOException("程序目录和用户目录均无法创建日志文件夹。", exception);
        }
    }
}
