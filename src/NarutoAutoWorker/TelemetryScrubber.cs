using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NarutoAutoGUI.Protocol;
using Sentry;

namespace NarutoAutoWorker;

// Every text bound for telemetry passes here, in the GUI as in the Worker: the profile folder is masked, then each
// password a Run used (PI inputs[].password, never a project's own names) is replaced, raw and JSON-escaped. The SDK
// hooks are the last gate: an item that still carries a password after scrubbing, or whose scrubbing fails, is
// dropped rather than sent.
internal sealed class TelemetryScrubber
{
    internal const string Filtered = "[Filtered]";
    private static readonly JavaScriptEncoder[] Encoders = [JavaScriptEncoder.Default,
        JavaScriptEncoder.UnsafeRelaxedJsonEscaping];
    private readonly object _gate = new();
    private string[] _forms = [];

    // Values stay for the process lifetime: an event may leave after its Run, and the Worker keeps the Run's Plan,
    // which holds the same values, as long.
    internal void Add(IEnumerable<string> passwords)
    {
        lock (_gate) {
            _forms = _forms.Concat(passwords.Where(value => value.Length != 0).SelectMany(value =>
                    Encoders.Select(encoder => JsonEncodedText.Encode(value, encoder).ToString()).Prepend(value)))
                .Distinct(StringComparer.Ordinal).OrderByDescending(form => form.Length).ToArray();
        }
    }

    internal string Scrub(string text)
    {
        var scrubbed = new StringBuilder(UserProfileMask.Apply(text));
        foreach (var form in Forms) {
            scrubbed.Replace(form, Filtered);
        }
        return scrubbed.ToString();
    }

    internal void Install(SentryOptions options, Action<Exception> fault)
    {
        // A breadcrumb repeats an earlier message on every later item and cannot be rewritten; none are kept.
        options.MaxBreadcrumbs = 0;
        options.SetBeforeSend(sentryEvent => Guard(sentryEvent, ScrubEvent, fault));
        options.SetBeforeSendTransaction(transaction => Guard(transaction, ScrubTransaction, fault));
        // A log's text is fixed once created, so callers scrub before logging and this only refuses leftovers.
        options.SetBeforeSendLog(log => Guard(log, item => Leaks(string.Join("\n",
            new[] { item.Message, item.Template }.Concat(item.Parameters.Select(value => $"{value}")))) ? null : item,
            fault));
    }

    private string[] Forms
    {
        get
        {
            lock (_gate) {
                return _forms;
            }
        }
    }

    private bool Leaks(string? text) =>
        text is not null && (Forms.Any(form => text.Contains(form, StringComparison.Ordinal))
            || UserProfileMask.Apply(text) != text);

    private static T? Guard<T>(T item, Func<T, T?> scrub, Action<Exception> fault) where T : class
    {
        try {
            return scrub(item);
        } catch (Exception exception) {
            fault(exception);
            return null;
        }
    }

    private SentryEvent? ScrubEvent(SentryEvent sentryEvent)
    {
        if (sentryEvent.Message is { } message) {
            sentryEvent.Message = new SentryMessage {
                Message = ScrubNullable(message.Message), Formatted = ScrubNullable(message.Formatted)
            };
        }
        foreach (var exception in sentryEvent.SentryExceptions ?? []) {
            exception.Value = ScrubNullable(exception.Value);
            foreach (var frame in exception.Stacktrace?.Frames ?? []) {
                frame.FileName = ScrubNullable(frame.FileName);
                frame.AbsolutePath = ScrubNullable(frame.AbsolutePath);
            }
        }
        foreach (var (key, value) in sentryEvent.Extra.ToArray()) {
            sentryEvent.SetExtra(key, ScrubValue(value));
        }
        foreach (var (key, value) in sentryEvent.Tags.ToArray()) {
            sentryEvent.SetTag(key, Scrub(value));
        }
        return Serialized(sentryEvent);
    }

    private SentryTransaction? ScrubTransaction(SentryTransaction transaction)
    {
        foreach (var span in transaction.Spans) {
            span.Description = ScrubNullable(span.Description);
            foreach (var (key, value) in span.Data.ToArray()) {
                span.SetData(key, ScrubValue(value));
            }
        }
        foreach (var (key, value) in transaction.Data.ToArray()) {
            transaction.SetData(key, ScrubValue(value));
        }
        foreach (var (key, value) in transaction.Tags.ToArray()) {
            transaction.SetTag(key, Scrub(value));
        }
        return Serialized(transaction);
    }

    // The item exactly as the SDK will write it; anything left after the field-by-field pass is not sent.
    private T? Serialized<T>(T item) where T : class, ISentryJsonSerializable
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            item.WriteTo(writer, null);
        }
        return Leaks(Encoding.UTF8.GetString(stream.ToArray())) ? null : item;
    }

    private string? ScrubNullable(string? text) => text is null ? null : Scrub(text);

    private object? ScrubValue(object? value) => value switch {
        null => null,
        string text => Scrub(text),
        bool or int or long or double or float or decimal => value,
        _ => JsonDocument.Parse(Scrub(JsonSerializer.Serialize(value))).RootElement.Clone()
    };
}
