using System.Text;
using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed class WorkerLogBuffer
{
    private const int MaximumEntries = 5000;
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly LinkedList<(WorkerLogEntry Entry, int Bytes)> _entries = new();
    private long _nextSequence = 1;
    private int _storedBytes;

    internal WorkerLogEntry Add(
        string level,
        string source,
        string message,
        Guid? runId = null,
        Guid? planItemId = null,
        string? taskName = null)
    {
        var (storedMessage, truncated, originalBytes) = Truncate(message);
        lock (_gate)
        {
            var entry = new WorkerLogEntry(
                _nextSequence++,
                DateTime.UtcNow,
                level,
                source,
                storedMessage,
                truncated,
                truncated ? originalBytes : null,
                runId,
                planItemId,
                taskName);
            var bytes = Encoding.UTF8.GetByteCount(storedMessage) + 256;
            _entries.AddLast((entry, bytes));
            _storedBytes += bytes;
            while (_entries.Count > MaximumEntries || _storedBytes > MaximumBytes)
            {
                var first = _entries.First!;
                _storedBytes -= first.Value.Bytes;
                _entries.RemoveFirst();
            }
            return entry;
        }
    }

    internal (long First, long Last) GetRange()
    {
        lock (_gate)
        {
            return (_entries.First?.Value.Entry.Sequence ?? _nextSequence, _entries.Last?.Value.Entry.Sequence ?? 0);
        }
    }

    internal LogGetSinceResponse GetSince(long afterSequence, int limit)
    {
        if (afterSequence < 0 || limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }
        var effectiveLimit = Math.Min(limit, 500);
        lock (_gate)
        {
            var first = _entries.First?.Value.Entry.Sequence ?? _nextSequence;
            var last = _entries.Last?.Value.Entry.Sequence ?? 0;
            var gap = afterSequence + 1 < first;
            var result = new List<WorkerLogEntry>(effectiveLimit);
            var responseBytes = 16 * 1024;
            foreach (var item in _entries.Where(item => item.Entry.Sequence > afterSequence))
            {
                var entryBytes = JsonSerializer.SerializeToUtf8Bytes(item.Entry, ProtocolJson.Options).Length + 1;
                if (result.Count >= effectiveLimit
                    || (result.Count > 0
                        && responseBytes + entryBytes > ProtocolConstants.MaximumLogGetSinceResponseBytes))
                {
                    break;
                }
                result.Add(item.Entry);
                responseBytes += entryBytes;
            }
            var returnedLast = result.LastOrDefault()?.Sequence ?? afterSequence;
            return new LogGetSinceResponse(
                result,
                effectiveLimit,
                first,
                last,
                returnedLast < last,
                gap,
                gap ? afterSequence + 1 : null,
                gap ? first - 1 : null);
        }
    }

    private static (string Message, bool Truncated, int OriginalBytes) Truncate(string message)
    {
        var originalBytes = Encoding.UTF8.GetByteCount(message);
        if (originalBytes <= ProtocolConstants.MaximumLogMessageBytes)
        {
            return (message, false, originalBytes);
        }

        var builder = new StringBuilder(message.Length);
        var bytes = 0;
        foreach (var rune in message.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > ProtocolConstants.MaximumLogMessageBytes)
            {
                break;
            }
            builder.Append(rune);
            bytes += runeBytes;
        }
        return (builder.ToString(), true, originalBytes);
    }
}
