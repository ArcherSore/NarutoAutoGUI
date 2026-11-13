namespace Gui.Infrastructure;

// Versions released under the earlier name kept the Configuration Set in config/maanop-config.json. The file is
// copied, not moved, so rolling back still finds it. This must run before anything asks whether the new file exists.
internal static class LegacyConfigFile
{
    private const string LegacyName = "maanop-config.json";

    internal static void Migrate(string path)
    {
        var legacy = Path.Combine(Path.GetDirectoryName(path)!, LegacyName);
        if (!File.Exists(path) && File.Exists(legacy)) {
            File.Copy(legacy, path);
        }
    }
}
