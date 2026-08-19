using System.Text.Json;
using NarutoAutoGUI.Models;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Infrastructure;

internal static class SelfTestRunner
{
    internal static int Run()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"NarutoAutoGUI-self-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(testDirectory);
            var logDirectory = Path.Combine(testDirectory, "logs");
            using var logger = new AppLogger(logDirectory);
            var projectDirectory = CreateProjectFixture(testDirectory);
            VerifySettings(logger, testDirectory, projectDirectory);
            VerifyProjectPlan(testDirectory, projectDirectory);
            VerifyProtocolFrame();

            logger.Debug("self-test-debug");
            logger.Info("self-test-info");
            logger.Dispose();
            var logText = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(logDirectory, "*.log").Select(File.ReadAllText));
            if (!logText.Contains("[DEBUG] self-test-debug", StringComparison.Ordinal)
                || !logText.Contains("[INFO] self-test-info", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DEBUG+ 文件日志验证失败。");
            }

            Console.WriteLine(
                "SELF-TEST PASS: settings v2 + legacy migration; PI default resolver; "
                + "MaaNOP Config v1; RunPlan digest; IPC framing; DEBUG+ file logging");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SELF-TEST FAIL: {exception}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
            }
            catch
            {
                // The isolated temporary directory can be cleaned by the OS later.
            }
        }
    }

    private static void VerifySettings(
        AppLogger logger,
        string testDirectory,
        string projectDirectory)
    {
        var settingsPath = Path.Combine(testDirectory, "settings.json");
        var store = new AppSettingsStore(logger, settingsPath);
        var legacyDirectory = Path.Combine(testDirectory, "legacy-maanop");
        var legacyAssets = Path.Combine(legacyDirectory, "assets");
        Directory.CreateDirectory(legacyAssets);
        File.WriteAllText(Path.Combine(legacyAssets, "interface.json"), "{}");
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(new
            {
                GameExecutablePath = Path.Combine(testDirectory, "QQGameLauncher.exe"),
                MaaNopExecutablePath = Path.Combine(legacyDirectory, "MFAAvalonia.exe")
            }));
        var migrated = store.Load();
        if (migrated.GameExecutablePath != AppSettings.DefaultGameExecutablePath
            || migrated.GameArguments != AppSettings.DefaultGameArguments
            || migrated.MaaNopProjectDirectory != Path.GetFullPath(legacyAssets))
        {
            throw new InvalidOperationException("旧版 Application Settings 内存迁移验证失败。");
        }

        var expected = new AppSettings
        {
            GameExecutablePath = @"C:\Test\Game.exe",
            GameArguments = "--test-game",
            MaaNopProjectDirectory = projectDirectory
        };
        store.Save(expected);
        var savedJson = File.ReadAllText(settingsPath);
        if (savedJson.Contains("MaaNopExecutablePath", StringComparison.Ordinal)
            || savedJson.Contains("GameWorkingDirectory", StringComparison.Ordinal)
            || !savedJson.Contains("\"SchemaVersion\": 2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Application Settings SchemaVersion 2 序列化验证失败。");
        }

        var actual = store.Load();
        if (actual.GameExecutablePath != expected.GameExecutablePath
            || actual.GameArguments != expected.GameArguments
            || actual.MaaNopProjectDirectory != expected.MaaNopProjectDirectory)
        {
            throw new InvalidOperationException("Application Settings 持久化往返验证失败。");
        }
    }

    private static void VerifyProjectPlan(string testDirectory, string projectDirectory)
    {
        var configPath = Path.Combine(testDirectory, "maanop-config.json");
        var project = ProjectPlanModule.Open(projectDirectory, configPath);
        if (project.Tasks.Count != 1 || !project.Tasks[0].DefaultOnlyValid)
        {
            throw new InvalidOperationException("PI default-only task catalog 验证失败。");
        }

        project.SelectTask("RealTask");
        using (var configDocument = JsonDocument.Parse(File.ReadAllBytes(configPath)))
        {
            var root = configDocument.RootElement;
            if (root.GetProperty("SchemaVersion").GetInt32() != 1
                || root.GetProperty("SelectedTasks")[0].GetString() != "RealTask"
                || root.GetProperty("ExplicitOptions").EnumerateObject().Any())
            {
                throw new InvalidOperationException("MaaNOP Config SchemaVersion 1 验证失败。");
            }
        }

        var attempt = project.CreateRunStartAttempt();
        if (attempt.Plan.Items.Count != 1
            || attempt.Plan.Items[0].TaskName != "RealTask"
            || attempt.Plan.ResolvedGlobalOptions.GetProperty("Server").GetProperty("server").GetString() != "1000"
            || attempt.Plan.Items[0].ResolvedOptions.GetProperty("Mode").GetString() != "Default"
            || attempt.Plan.Items[0].ResolvedOptions.GetProperty("Nested").GetString() != "On"
            || attempt.PlanDigest != CanonicalDigest.ComputePlanDigestV1(attempt.Plan))
        {
            throw new InvalidOperationException("正式 PI Resolver / RunPlan / planDigest 验证失败。");
        }
    }

    private static void VerifyProtocolFrame()
    {
        using var stream = new MemoryStream();
        var requestId = Guid.NewGuid();
        var envelope = WireEnvelope.Request(
            ProtocolOperations.WorkerGetSnapshot,
            requestId,
            new { });
        var writer = new ProtocolConnection(stream);
        writer.WriteAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
        stream.Position = 0;
        var reader = new ProtocolConnection(stream);
        var decoded = reader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (decoded?.RequestId != requestId
            || decoded.Operation != ProtocolOperations.WorkerGetSnapshot)
        {
            throw new InvalidOperationException("Named Pipe frame round-trip 验证失败。");
        }
    }

    private static string CreateProjectFixture(string testDirectory)
    {
        var projectDirectory = Path.Combine(testDirectory, "project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "agent"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "resource"));
        File.WriteAllText(
            Path.Combine(projectDirectory, "interface.json"),
            """
            {
              "interface_version": 2,
              "name": "SelfTestProject",
              "version": "1.0.0",
              "controller": [{
                "name": "Win32",
                "type": "Win32",
                "win32": {
                  "class_regex": ".*",
                  "window_regex": "SelfTestWindow",
                  "screencap": "PrintWindow",
                  "mouse": "Seize",
                  "keyboard": "Seize"
                }
              }],
              "resource": [{"name": "Default", "path": ["./resource"]}],
              "agent": {"child_exec": "python", "child_args": ["./agent/main.py"]},
              "global_option": ["Server"],
              "task": [{
                "name": "RealTask",
                "label": "Real task",
                "entry": "SelfTestEntry",
                "option": ["Mode"],
                "pipeline_override": {"SelfTestEntry": {"enabled": true}}
              }],
              "option": {
                "Server": {
                  "type": "input",
                  "inputs": [{
                    "name": "server",
                    "default": "1000",
                    "pipeline_type": "int",
                    "verify": "^[0-9]+$"
                  }],
                  "pipeline_override": {"SelfTestEntry": {"server": "{server}"}}
                },
                "Mode": {
                  "type": "select",
                  "default_case": "Default",
                  "cases": [{
                    "name": "Default",
                    "option": ["Nested"],
                    "pipeline_override": {"SelfTestEntry": {"mode": "default"}}
                  }]
                },
                "Nested": {
                  "type": "switch",
                  "default_case": "On",
                  "cases": [
                    {"name": "On", "pipeline_override": {"SelfTestEntry": {"nested": true}}},
                    {"name": "Off", "pipeline_override": {"SelfTestEntry": {"nested": false}}}
                  ]
                }
              }
            }
            """);
        return Path.GetFullPath(projectDirectory);
    }
}
