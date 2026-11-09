using System.Text.Json.Nodes;
using NarutoAutoGUI.ProjectModel;

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
            "42", "[]", "{}", """{"sentry":"x"}""", """{"sentry":{}}""", """{"sentry":{"dsn":5}}""",
            """{"sentry":{"dsn":"  "}}""", """{"sentry":{"dsn":null}}""" }) {
            Require(Load(JsonNode.Parse(invalid)).Telemetry is null, $"无效遥测配置只关闭遥测：{invalid}");
        }
    }
}
