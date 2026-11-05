using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    private const string OutputTemplate = "[{Time:l}] [{LevelName:l}] [T{ThreadId}] [{Source:l}] {Message:l}{NewLine}";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

    // Logs entering a stretch of work and, when disposed, leaving it with how long it took.
    internal IDisposable Scope(
        string name, [CallerFilePath] string file = "", [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
    {
        var source = FormatSource(file, member, line);
        Emit(LogLevel.Info, source, $"{name} | enter", null);
        return new LogScope(this, source, name);
    }

    // Worker logs are relayed here, so the local call site says nothing about where they came from, and the local clock
    // nothing about when: a log recovered after a reconnect or a GUI restart keeps the time the Worker wrote it.
    internal void WriteWorker(LogLevel level, string message, DateTimeOffset timestamp) =>
        Emit(level, "Worker", message, null, timestamp);

    // One diagnostic field of a message; a missing value leaves no empty brackets behind.
    internal static string Field(string name, object? value) =>
        value is null or "" ? string.Empty : $" [{name}={value}]";

    // A profile folder is usually named after the Windows account, which stays out of the logs. JSON doubles the
    // backslashes in a path, and a longer sibling such as C:\Users\name2 is a different folder.
    internal static string MaskUserProfile(string text, string profile)
    {
        if (profile.Length == 0) {
            return text;
        }

        var segments = profile.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
        var pattern = string.Join(@"[\\/]+", segments) + @"(?![\p{L}\p{N}_-]|\.[\p{L}\p{N}])";
        return Regex.Replace(text, pattern, "%USERPROFILE%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

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

    private void Emit(
        LogLevel level, string source, string message, Exception? exception, DateTimeOffset? timestamp = null)
    {
        var text = MaskUserProfile(message, UserProfile);
        var entry = new LogEntry(timestamp ?? DateTimeOffset.Now, level, text);
        // Serilog would render an exception after this point, where its paths could no longer be masked.
        var rendered = exception is null
            ? text : $"{text}{Environment.NewLine}{MaskUserProfile(exception.ToString(), UserProfile)}";
        lock (_sync) {
            if (_disposed) {
                return;
            }

            try {
                _logger.ForContext("Time", FormatTime(entry.Timestamp))
                    .ForContext("LevelName", entry.DisplayLevel)
                    .ForContext("ThreadId", Environment.CurrentManagedThreadId)
                    .ForContext("Source", source)
                    .Write(ToSerilogLevel(level), "{Text:l}", rendered);
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

    // The file name carries the date, so only a relayed log from another day needs its own.
    private static string FormatTime(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        var format = local.Date == DateTime.Today ? "HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss.fff";
        return local.ToString(format, CultureInfo.InvariantCulture);
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

    private sealed class LogScope(AppLogger logger, string source, string name) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public void Dispose() => logger.Emit(
            LogLevel.Info, source, $"{name} | leave, {Stopwatch.GetElapsedTime(_started).TotalMilliseconds:0}ms", null);
    }

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
