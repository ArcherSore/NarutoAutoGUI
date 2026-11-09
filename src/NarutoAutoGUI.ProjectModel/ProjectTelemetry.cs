using System.Text.Json;

namespace NarutoAutoGUI.ProjectModel;

// PI v2.9 telemetry.sentry as the project declares it; whether anything is sent is decided by the GUI's gate.
public sealed record ProjectTelemetry(
    string Dsn, string? Environment, bool Tracing, double TracesSampleRate, double FailureAttachmentsSampleRate)
{
    // Read like MXU and MFAA: unknown fields are ignored, a missing or mistyped optional field takes its default and a
    // rate is clamped to 0..1. Telemetry does not affect execution (ADR 0002), so an invalid value only disables it.
    internal static ProjectTelemetry? Read(JsonElement root)
    {
        if (!root.TryGetProperty("telemetry", out var telemetry) || telemetry.ValueKind != JsonValueKind.Object
            || !telemetry.TryGetProperty("sentry", out var sentry) || sentry.ValueKind != JsonValueKind.Object
            || Text(sentry, "dsn") is not { } dsn) {
            return null;
        }
        return new ProjectTelemetry(dsn, Text(sentry, "environment"),
            sentry.TryGetProperty("tracing", out var tracing) && tracing.ValueKind == JsonValueKind.False ? false : true,
            Rate(sentry, "traces_sample_rate"), Rate(sentry, "failure_attachments_sample_rate"));
    }

    private static string? Text(JsonElement sentry, string name) =>
        sentry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

    private static double Rate(JsonElement sentry, string name) =>
        sentry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? Math.Clamp(value.GetDouble(), 0, 1) : 1.0;
}
