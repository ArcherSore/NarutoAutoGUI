using System.Text.Json.Nodes;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;
using NarutoAutoGUI.Settings;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private const string TestDsn = "https://public@o1.ingest.sentry.io/1";

    private static JsonNode Json(string text) => JsonNode.Parse(text.Replace("@DSN@", TestDsn))!;

    // PI v2.9 telemetry is read like MXU and MFAA: unknown fields are ignored, missing ones take their defaults and
    // rates are clamped. Telemetry never affects execution, so no telemetry value can stop the project from loading.
    private static void VerifyProjectTelemetry(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "telemetry"));
        ProjectPlanModule Load(JsonNode? telemetry)
        {
            EditInterface(directory, root => {
                if (telemetry is null) {
                    root.Remove("telemetry");
                } else {
                    root["telemetry"] = telemetry;
                }
            });
            var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "telemetry-config.json"));
            Require(project.Tasks.Count > 0 && project.CreateRunStartAttempt().Plan.Items.Count > 0,
                $"遥测配置不能影响任务加载和运行：{telemetry?.ToJsonString()}");
            return project;
        }

        Require(Load(null).Telemetry is null, "未声明 telemetry 时不启用遥测。");
        Require(Load(Json("""
            {"sentry":{"dsn":"@DSN@","environment":"beta","tracing":false,"traces_sample_rate":0.25,
             "failure_attachments_sample_rate":0.5}}
            """)).Telemetry == new ProjectTelemetry(TestDsn, "beta", false, 0.25, 0.5), "完整配置应原样读取。");
        Require(Load(Json("""{"sentry":{"dsn":" @DSN@ "}}"""))
            .Telemetry == new ProjectTelemetry(TestDsn, null, true, 1.0, 1.0), "缺失的可选字段应使用默认值。");
        Require(Load(Json("""
            {"future":{"x":1},"sentry":{"dsn":"@DSN@","traces_sample_rate":5,
             "failure_attachments_sample_rate":-1,"unknown":true}}
            """)).Telemetry == new ProjectTelemetry(TestDsn, null, true, 1.0, 0.0), "采样率越界应截断，未知字段忽略。");
        Require(Load(Json("""
            {"sentry":{"dsn":"@DSN@","environment":3,"tracing":"no","traces_sample_rate":"0.5",
             "failure_attachments_sample_rate":null}}
            """)).Telemetry == new ProjectTelemetry(TestDsn, null, true, 1.0, 1.0), "类型错误的可选字段应使用默认值。");
        foreach (var invalid in new[] {
            "null",
            "42", "[]", "{}", """{"sentry":"x"}""", """{"sentry":{}}""", """{"sentry":{"dsn":5}}""",
            """{"sentry":{"dsn":"  "}}""", """{"sentry":{"dsn":null}}""" }) {
            Require(Load(JsonNode.Parse(invalid)).Telemetry is null, $"无效遥测配置只关闭遥测：{invalid}");
        }
    }

    // Only a released GUI with a released project sends. FORCE lifts the development-version checks alone: the user's
    // choice still decides, and a self-test never sends.
    private static void VerifyTelemetryGate()
    {
        var project = new ProjectTelemetry(TestDsn, null, true, 0.5, 0.25);
        var release = new TelemetryBuild("1.12.2+sha.abc", DebugBuild: false, SelfTest: false, Force: false);
        TelemetryOptions? Manifest(TelemetryBuild build, string projectVersion = "v3.3.0", bool enabled = true,
            ProjectTelemetry? telemetry = null) =>
            TelemetryGate.CreateOptions(build, telemetry ?? project, "MaaNOP", projectVersion, enabled);

        Require(Manifest(release) == new TelemetryOptions(TestDsn, "NarutoAutoGUI@1.12.2+MaaNOP@v3.3.0",
                "production", true, 0.5, 0.25) { Enabled = true }
            && TelemetryGate.IsAvailable(release, project, "v3.3.0"), "发布版应生成完整的遥测配置。");
        Require(Manifest(release, enabled: false) is { Enabled: false },
            "用户关闭时仍下发配置，以便运行中重新开启；Worker 只在 Enabled 时采集。");
        Require(Manifest(release, telemetry: project with { Environment = "beta" })?.Environment == "beta",
            "PI 声明的 environment 优先于默认值。");
        Require(Manifest(release, "1.0.0-beta.2") is not null && Manifest(release, "v2.0.0-rc.1") is not null
            && Manifest(release with { GuiVersion = "1.13.0-rc.1" }) is not null && Manifest(release, "") is not null,
            "beta/rc 预发布版本和未声明的项目版本不属于开发版本。");
        Require(TelemetryGate.CreateOptions(release, null, "MaaNOP", "v3.3.0", true) is null
            && !TelemetryGate.IsAvailable(release, null, "v3.3.0"), "未声明 DSN 时不启用。");

        var development = new[] {
            (release with { DebugBuild = true }, "v3.3.0"), (release with { GuiVersion = "0.1.0+abc" }, "v3.3.0"),
            (release with { GuiVersion = "0.1.0-ci.12+sha.abc" }, "v3.3.0"),
            (release with { GuiVersion = "" }, "v3.3.0"), (release with { GuiVersion = "1.13.0-alpha.1" }, "v3.3.0"),
            (release, "DEBUG_VERSION"), (release, "v0.9.0"), (release, "v3.3.0-ci.4") };
        foreach (var (build, projectVersion) in development) {
            Require(Manifest(build, projectVersion) is null
                && !TelemetryGate.IsAvailable(build, project, projectVersion),
                $"开发版本必须禁用遥测：{build} / {projectVersion}");
            Require(Manifest(build with { Force = true }, projectVersion) is { Enabled: true }
                && Manifest(build with { Force = true }, projectVersion, enabled: false) is { Enabled: false },
                $"FORCE 只放行开发版本闸门，不改变用户选择：{build} / {projectVersion}");
        }
        foreach (var build in new[] { release with { SelfTest = true }, release with { SelfTest = true, Force = true },
            release with { SelfTest = true, Force = true, DebugBuild = true } }) {
            Require(Manifest(build) is null && !TelemetryGate.IsAvailable(build, project, "v3.3.0"),
                $"自检模式必须强制禁用，FORCE 也不能放行：{build}");
        }
    }

    // "帮助改进软件" starts on, is remembered, reaches a running Worker like the framework switches, and is unavailable
    // wherever the gate blocks telemetry.
    private static void VerifyTelemetryPreference(AppLogger logger, string testDirectory)
    {
        var directory = Path.Combine(testDirectory, "telemetry-preference");
        ApplicationSettings Create() => CreateIdleSettings(directory, logger);
        var settings = Create();
        Require(settings.HelpImprove.Value && !Directory.Exists(directory), "帮助改进软件默认开启，读取不得写配置。");
        var changes = new List<SettingsToggle>();
        settings.FrameworkOptionsChanged += changes.Add;
        settings.HelpImprove.Value = false;
        Require(!Create().HelpImprove.Value && changes.SequenceEqual([settings.HelpImprove]),
            "关闭应持久化，并像框架开关一样同步到运行中的 Worker。");
        settings.HelpImprove.Value = true;
        Require(Create().HelpImprove.Value, "重新开启应持久化。");

        settings.ShowTelemetryAvailability(false);
        Require(!settings.HelpImprove.IsEnabled && settings.HelpImprove.Value, "闸门禁用时开关不可用，但保留用户选择。");
        settings.ShowTelemetryAvailability(true);
        Require(settings.HelpImprove.IsEnabled, "可用时恢复开关。");
    }
}
