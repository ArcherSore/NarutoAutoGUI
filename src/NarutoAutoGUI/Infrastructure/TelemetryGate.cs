using System.Text.RegularExpressions;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Infrastructure;

// What the release gate needs to know about this process. Force is NARUTO_TELEMETRY_FORCE=1, for manual checks.
internal sealed record TelemetryBuild(string GuiVersion, bool DebugBuild, bool SelfTest, bool Force)
{
    internal static bool SelfTestProcess { get; set; }

    internal static TelemetryBuild Current => new(App.ApplicationVersion,
#if DEBUG
        DebugBuild: true,
#else
        DebugBuild: false,
#endif
        SelfTestProcess, Environment.GetEnvironmentVariable("NARUTO_TELEMETRY_FORCE") == "1");
}

// The only place deciding whether telemetry may run (ADR 0009): a released GUI and a released project, never in a
// self-test. Force lifts the development-version checks alone; the user's choice still travels as Enabled.
internal static partial class TelemetryGate
{
    internal static bool IsAvailable(TelemetryBuild build, ProjectTelemetry? telemetry, string projectVersion) =>
        telemetry is not null && !build.SelfTest
        && (build.Force || !build.DebugBuild && !IsDevelopment(build.GuiVersion, emptyIsDevelopment: true)
            && !IsDevelopment(projectVersion, emptyIsDevelopment: false));

    internal static TelemetryOptions? CreateOptions(TelemetryBuild build, ProjectTelemetry? telemetry,
        string projectName, string projectVersion, bool enabled) =>
        IsAvailable(build, telemetry, projectVersion)
            ? new TelemetryOptions(telemetry!.Dsn, $"NarutoAutoGUI@{build.GuiVersion.Split('+')[0]}+{projectName}@"
                + projectVersion, telemetry.Environment ?? "production", telemetry.Tracing, telemetry.TracesSampleRate,
                telemetry.FailureAttachmentsSampleRate) { Enabled = enabled }
            : null;

    // As MXU and MFAA judge a project: DEBUG_VERSION, below 1.0.0, or a prerelease other than beta or rc.
    private static bool IsDevelopment(string version, bool emptyIsDevelopment)
    {
        var text = version.Trim();
        if (text.Length == 0) {
            return emptyIsDevelopment;
        }
        if (text == "DEBUG_VERSION" || SemanticVersion().Match(text) is not { Success: true } match) {
            return true;
        }
        if (int.Parse(match.Groups["major"].Value) < 1) {
            return true;
        }
        return match.Groups["pre"].Success
            && !match.Groups["pre"].Value.Split('.').Any(tag => tag is "beta" or "rc");
    }

    [GeneratedRegex(@"\A[vV]?(?<major>\d+)(?:\.\d+){0,2}(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]*)?\z")]
    private static partial Regex SemanticVersion();
}
