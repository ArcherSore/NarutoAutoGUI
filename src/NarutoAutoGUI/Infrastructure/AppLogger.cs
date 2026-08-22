using System.Diagnostics;
using System.Text;
using NarutoAutoGUI.Models;

namespace NarutoAutoGUI.Infrastructure;

internal sealed class AppLogger : IDisposable
{
    private const long MaximumFileBytes = 10L * 1024L * 1024L;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private readonly object _sync = new();
    private readonly string _logDirectory;
    private StreamWriter? _writer;
    private DateOnly _fileDate;
    private int _fileSequence;
    private bool _disposed;

    internal AppLogger(string? logDirectory = null)
    {
        string? fallbackMessage = null;
        if (logDirectory is not null)
        {
            _logDirectory = logDirectory;
            Directory.CreateDirectory(_logDirectory);
        }
        else
        {
            (_logDirectory, fallbackMessage) = ResolveDefaultLogDirectory();
        }

        Directory.CreateDirectory(_logDirectory);
        DeleteExpiredFiles();
        if (fallbackMessage is not null)
        {
            Warn(fallbackMessage);
        }
    }

    internal event EventHandler<LogEntry>? EntryWritten;

    internal string LogDirectory => _logDirectory;

    internal void Debug(string message) => Write(LogLevel.Debug, message);

    internal void Info(string message) => Write(LogLevel.Info, message);

    internal void Warn(string message, Exception? exception = null) =>
        Write(LogLevel.Warn, message, exception);

    internal void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, message, exception);

    internal void Critical(string message, Exception? exception = null) =>
        Write(LogLevel.Critical, message, exception);

    internal void Write(LogLevel level, string message, Exception? exception = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        var line = entry.ToString();
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
                _writer!.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException)
            {
                Debugger.Log(0, "NarutoAutoGUI", $"File logging failed: {loggingException}\n");
            }
        }

        try
        {
            EntryWritten?.Invoke(this, entry);
        }
        catch (Exception subscriberException)
        {
            Debugger.Log(0, "NarutoAutoGUI", $"Log subscriber failed: {subscriberException}\n");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void EnsureWriter(int nextWriteBytes)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is null || _fileDate != today)
        {
            OpenWriter(today, sequence: 0);
        }

        if (_writer!.BaseStream.Length + nextWriteBytes <= MaximumFileBytes)
        {
            return;
        }

        OpenWriter(today, _fileSequence + 1);
    }

    private void OpenWriter(DateOnly date, int sequence)
    {
        _writer?.Dispose();
        _fileDate = date;
        _fileSequence = sequence;

        string path;
        do
        {
            var suffix = _fileSequence == 0 ? string.Empty : $".{_fileSequence}";
            path = Path.Combine(_logDirectory, $"NarutoAutoGUI-{date:yyyyMMdd}{suffix}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumFileBytes)
            {
                break;
            }

            _fileSequence++;
        } while (true);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        DeleteExpiredFiles();
    }

    private void DeleteExpiredFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var path in Directory.EnumerateFiles(_logDirectory, "NarutoAutoGUI-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debugger.Log(0, "NarutoAutoGUI", $"Log retention cleanup failed: {exception}\n");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debugger.Log(0, "NarutoAutoGUI", $"Log enumeration failed: {exception}\n");
        }
    }

    private static (string Path, string? Warning) ResolveDefaultLogDirectory()
    {
        var preferred = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(preferred);
            return (preferred, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var candidates = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NarutoAutoGUI",
                    "logs"),
                Path.Combine(Path.GetTempPath(), "NarutoAutoGUI", "logs")
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    return (
                        candidate,
                        $"程序目录日志不可写，已回退到：{candidate}。原因：{exception.Message}");
                }
                catch (Exception candidateException) when (candidateException is IOException
                                                                 or UnauthorizedAccessException)
                {
                    // Try the next user-writable location.
                }
            }

            throw new IOException("程序目录和用户目录均无法创建日志文件夹。", exception);
        }
    }
}
