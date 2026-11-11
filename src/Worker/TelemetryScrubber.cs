using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Protocol;
using Sentry;
using Sentry.Extensibility;

namespace Worker;

// Every text bound for telemetry passes here: the profile folder is masked, then each password a Run used (PI
// inputs[].password, never a project's own names) is replaced, raw and JSON-escaped. The SDK hooks are the last gate:
// an item whose text still carries a password or the profile folder after scrubbing, or whose scrubbing fails, is
// dropped rather than sent.
internal sealed partial class TelemetryScrubber
{
    internal const string Filtered = "[Filtered]";
    private static readonly JavaScriptEncoder[] Encoders = [JavaScriptEncoder.Default,
        JavaScriptEncoder.UnsafeRelaxedJsonEscaping];
    // Written by the SDK itself, never from what a Run reports: a short numeric password can match their digits.
    private static readonly HashSet<string> GeneratedFields = new(StringComparer.Ordinal) {
        "timestamp", "start_timestamp", "event_id", "trace_id", "span_id", "parent_span_id", "boot_time",
        "app_start_time", "release", "debug_id", "code_id", "image_addr", "image_size"
    };
    private readonly object _gate = new();
    private string[] _forms = [];

    // Values stay for the process lifetime: an event may leave after its Run, and the Worker keeps the Run's Plan,
    // which holds the same values, as long.
    internal void Add(IEnumerable<string> passwords)
    {
        lock (_gate) {
            var forms = new HashSet<string>(_forms, StringComparer.Ordinal);
            foreach (var password in passwords.Where(value => value.Length != 0)) {
                forms.Add(password);
                // Encoders differ in what they escape and in the case of the hex digits: Python writes \u5bc6
                // where .NET writes \u5BC6.
                var escaped = Encoders.Select(encoder => JsonEncodedText.Encode(password, encoder).ToString())
                    .Append(AsciiEscaped(password));
                foreach (var form in escaped) {
                    forms.Add(form);
                    forms.Add(UnicodeEscape().Replace(form, match => match.Value.ToLowerInvariant()));
                    forms.Add(UnicodeEscape().Replace(form, match => "\\u" + match.Value[2..].ToUpperInvariant()));
                }
            }
            // Longer forms first, so a password inside another is not replaced in part.
            _forms = forms.OrderByDescending(form => form.Length).ToArray();
        }
    }

    // JSON with every non-ASCII character escaped, as Python's json.dumps writes it by default.
    private static string AsciiEscaped(string text)
    {
        var escaped = new StringBuilder();
        foreach (var character in text) {
            escaped.Append(character switch {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or > '\x7F' => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });
        }
        return escaped.ToString();
    }

    [GeneratedRegex(@"\\u[0-9A-Fa-f]{4}")]
    private static partial Regex UnicodeEscape();

    // string.Replace returns the same instance when a form is absent, so clean text costs no allocation.
    internal string Scrub(string text) =>
        Forms.Aggregate(UserProfileMask.Apply(text), (scrubbed, form) => scrubbed.Replace(form, Filtered));

    // The privacy settings both processes share (ADR 0009): no PII, no machine name, the anonymous user and this
    // scrubber as the last gate. Each caller adds only what differs.
    internal IDisposable InitSdk(TelemetryOptions telemetry, ITransport? transport, Action<Exception> fault,
        Func<bool> sending, Action<SentryOptions> configure)
    {
        var sdk = SentrySdk.Init(sentry => {
            sentry.Dsn = telemetry.Dsn;
            sentry.Release = telemetry.Release;
            sentry.Environment = telemetry.Environment;
            sentry.SendDefaultPii = false;
            sentry.ServerName = string.Empty;
            sentry.AttachStacktrace = false;
            sentry.IsGlobalModeEnabled = true;
            Install(sentry, fault, sending);
            configure(sentry);
            if (transport is not null) {
                sentry.Transport = transport;
            }
        });
        SentrySdk.ConfigureScope(scope => scope.User = new SentryUser { Id = TelemetryIdentity.AnonymousUserId() });
        return sdk;
    }

    // Nothing leaves while sending is off, including what the SDK captures on its own.
    private void Install(SentryOptions options, Action<Exception> fault, Func<bool> sending)
    {
        // A breadcrumb repeats an earlier message on every later item; telemetry has no use for them.
        options.MaxBreadcrumbs = 0;
        options.SetBeforeSend(sentryEvent => sending() ? Guard(sentryEvent, ScrubEvent, fault) : null);
        options.SetBeforeSendTransaction(transaction =>
            sending() ? Guard(transaction, ScrubTransaction, fault) : null);
        options.SetBeforeSendLog(log => sending() ? Guard(log, CheckLog, fault) : null);
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

    private bool Leaks(string text) =>
        Forms.Any(form => text.Contains(form, StringComparison.Ordinal)) || UserProfileMask.Apply(text) != text;

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
        // Loaded modules are named by path, and the application may live under the profile folder.
        foreach (var image in sentryEvent.DebugImages ?? []) {
            image.CodeFile = ScrubNullable(image.CodeFile);
            image.DebugFile = ScrubNullable(image.DebugFile);
        }
        foreach (var (key, value) in sentryEvent.Extra.ToArray()) {
            sentryEvent.SetExtra(key, ScrubValue(value));
        }
        foreach (var (key, value) in sentryEvent.Tags.ToArray()) {
            sentryEvent.SetTag(key, Scrub(value));
        }
        // Grouping may name the node that failed, and node names are the project's own text.
        sentryEvent.Fingerprint = sentryEvent.Fingerprint.Select(Scrub).ToArray();
        return Checked(sentryEvent);
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
        return Checked(transaction);
    }

    // A log's text is fixed once created, so callers scrub before logging and this only refuses leftovers.
    private SentryLog? CheckLog(SentryLog log)
    {
        var texts = new[] { log.Message, log.Template }.Concat(log.Parameters.Select(value => $"{value}"));
        return texts.Any(text => text is not null && Leaks(text)) ? null : log;
    }

    // Every text of the item as the SDK will write it, decoded; numbers and SDK-written fields are not Run content.
    private T? Checked<T>(T item) where T : class, ISentryJsonSerializable
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            item.WriteTo(writer, null);
        }
        return Texts(JsonNode.Parse(stream.ToArray()), null).Any(Leaks) ? null : item;
    }

    private static IEnumerable<string> Texts(JsonNode? node, string? name) => node switch {
        JsonObject values => values.SelectMany(property => Texts(property.Value, property.Key)),
        JsonArray items => items.SelectMany(item => Texts(item, name)),
        JsonValue value when value.GetValueKind() == JsonValueKind.String && !GeneratedFields.Contains(name ?? "") =>
            [value.GetValue<string>()],
        _ => []
    };

    private string? ScrubNullable(string? text) => text is null ? null : Scrub(text);

    // Structured values are scrubbed text by text, so a number stays a number and the JSON stays valid.
    private object? ScrubValue(object? value) => value switch {
        null => null,
        string text => Scrub(text),
        bool or int or long or double or float or decimal => value,
        _ => JsonSerializer.SerializeToElement(ScrubNode(JsonSerializer.SerializeToNode(value)))
    };

    private JsonNode? ScrubNode(JsonNode? node)
    {
        switch (node) {
            case JsonObject values:
                foreach (var (key, child) in values.ToArray()) {
                    values[key] = ScrubNode(child?.DeepClone());
                }
                return values;
            case JsonArray items:
                for (var index = 0; index < items.Count; index++) {
                    items[index] = ScrubNode(items[index]?.DeepClone());
                }
                return items;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(Scrub(value.GetValue<string>()));
            default:
                return node;
        }
    }
}
