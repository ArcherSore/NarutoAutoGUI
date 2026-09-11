using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NarutoAutoGUI.Updates;

public sealed record PreparedUpdate(string Installation, string Staging, string Tag, string Notes);
public sealed record UpdateHandoff(PreparedUpdate Update, int GuiPid);
public sealed record UpdateCompletion(string Tag, string Notes);

public static class UpdateStorage
{
    public static string DirectoryFor(string installation)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation)).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..24];
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaaNOP", "updates", key);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static FileStream AcquireInstallLock(string installation) => new(
        Path.Combine(DirectoryFor(installation), "install.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);

    public static void SaveCompletion(PreparedUpdate update)
    {
        File.WriteAllText(Path.Combine(DirectoryFor(update.Installation), "completed.json"),
            JsonSerializer.Serialize(new UpdateCompletion(update.Tag, update.Notes)));
    }

    public static UpdateCompletion? TakeCompletion(string installation, string currentVersion)
    {
        var path = Path.Combine(DirectoryFor(installation), "completed.json");
        if (!File.Exists(path)) {
            return null;
        }
        var completion = JsonSerializer.Deserialize<UpdateCompletion>(File.ReadAllText(path));
        var matches = completion is not null && SemanticVersion.Parse(completion.Tag)
            .CompareTo(SemanticVersion.Parse(currentVersion)) == 0;
        File.Delete(path);
        return matches ? completion : null;
    }
}
