using System.Text.Json;
using NarutoAutoGUI.Models;

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
            var settingsPath = Path.Combine(testDirectory, "settings.json");
            var store = new AppSettingsStore(logger, settingsPath);
            File.WriteAllText(
                settingsPath,
                JsonSerializer.Serialize(new
                {
                    GameExecutablePath = @"C:\Legacy\QQGameLauncher.exe",
                    MaaNopExecutablePath = @"C:\Legacy\MaaNOP.exe"
                }));
            var migrated = store.Load();
            if (migrated.GameExecutablePath != AppSettings.DefaultGameExecutablePath
                || migrated.GameArguments != AppSettings.DefaultGameArguments)
            {
                throw new InvalidOperationException("旧版游戏启动配置迁移验证失败。");
            }

            var expected = new AppSettings
            {
                GameExecutablePath = @"C:\Test\Game.exe",
                GameArguments = "--test-game",
                MaaNopExecutablePath = @"C:\Test\MaaNOP.exe"
            };

            store.Save(expected);
            if (File.ReadAllText(settingsPath).Contains("GameWorkingDirectory", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("配置文件不应包含工作目录字段。");
            }

            var actual = store.Load();
            if (actual.GameExecutablePath != expected.GameExecutablePath
                || actual.GameArguments != expected.GameArguments
                || actual.MaaNopExecutablePath != expected.MaaNopExecutablePath)
            {
                throw new InvalidOperationException("配置持久化往返验证失败。");
            }

            var defaults = new AppSettings();
            if (defaults.GameExecutablePath != AppSettings.DefaultGameExecutablePath
                || defaults.GameArguments != AppSettings.DefaultGameArguments)
            {
                throw new InvalidOperationException("火影忍者 Online 默认启动配置验证失败。");
            }

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
                "SELF-TEST PASS: legacy game migration; game launch defaults; "
                + "settings round-trip; DEBUG+ file logging");
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
}
