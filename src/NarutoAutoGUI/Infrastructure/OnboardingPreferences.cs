using System.Globalization;

namespace NarutoAutoGUI.Infrastructure;

internal sealed class OnboardingPreferences
{
    private const int CurrentVersion = 1;
    private readonly string _directory;
    private readonly AppLogger _logger;
    private string VersionPath => Path.Combine(_directory, "onboarding.txt");
    private string PendingPath => Path.Combine(_directory, "onboarding-new-user.pending");

    internal OnboardingPreferences(string directory, AppLogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    internal bool ShouldOfferAutomatically { get; private set; }

    internal void CaptureBeforeProjectLoad(string configPath)
    {
        try {
            if (ReadVersion() >= CurrentVersion) {
                return;
            }
            var missing = !Exists(configPath);
            ShouldOfferAutomatically = missing || Exists(PendingPath);
            if (missing) {
                Directory.CreateDirectory(_directory);
                using var marker = new FileStream(PendingPath, FileMode.OpenOrCreate, FileAccess.Write);
                marker.Flush(flushToDisk: true);
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException) {
            _logger.Warn("无法读取或保留首次新手指引资格。", exception);
        }
    }

    internal bool MarkHandled()
    {
        var temporary = Path.Combine(_directory, $".onboarding.{Guid.NewGuid():N}.tmp");
        try {
            if (ReadVersion() < CurrentVersion) {
                Directory.CreateDirectory(_directory);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) {
                    var text = CurrentVersion.ToString(CultureInfo.InvariantCulture);
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, VersionPath, overwrite: true);
            }
            ShouldOfferAutomatically = false;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException) {
            _logger.Warn("保存新手指引完成版本失败。", exception);
            return false;
        } finally {
            TryDelete(temporary);
        }
        TryDelete(PendingPath);
        return true;
    }

    private int ReadVersion()
    {
        try {
            var text = File.ReadAllText(VersionPath).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                ? version : throw new InvalidDataException("新手指引版本不是非负整数。");
        } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
            return 0;
        }
    }

    private static bool Exists(string path)
    {
        try {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) {
                throw new IOException("新手指引或任务配置文件路径指向目录。");
            }
            return true;
        } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try {
            File.Delete(path);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _logger.Warn("清理新手指引临时文件失败。", exception);
        }
    }
}
