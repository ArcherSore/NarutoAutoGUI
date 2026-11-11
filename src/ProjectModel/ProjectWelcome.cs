using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProjectModel;

public sealed record ProjectWelcome(string Title, IReadOnlyList<string> Contents, string Hash);

// Follows MXU's welcome rules, without translations or URLs: a `$` key shows as its name.
internal static class ProjectWelcomeResolver
{
    private sealed record Entry(string Source, string Content);

    internal static ProjectWelcome? Resolve(ProjectDefinition project, Action<string> warn)
    {
        var entries = project.Welcome
            .Select(source => new Entry(source, ReadContent(source, project.ProjectRoot, warn))).ToArray();
        var contents = entries.Select(entry => entry.Content).Where(content => content.Length > 0).ToArray();
        if (contents.Length == 0) {
            return null;
        }
        return new ProjectWelcome(project.About.Name, contents, ComputeHash(entries));
    }

    // A single announcement is keyed by its content alone, so renaming its file does not prompt again.
    private static string ComputeHash(Entry[] entries) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(entries.Length == 1 ? entries[0].Content : JsonSerializer.Serialize(entries))));

    private static string ReadContent(string source, string projectRoot, Action<string> warn)
    {
        var text = ProjectContent.StripKeyMarker(source);
        if (ProjectContent.Classify(text) == FileReference.None) {
            return text;
        }
        try {
            return ProjectContent.ReadFile(projectRoot, text);
        } catch (Exception exception) {
            // Whether a string names a file is a guess, so one that cannot be read is shown as the text it may be.
            warn($"读取公告文件失败，已按文本显示。source={text}；{exception.Message}");
            return text;
        }
    }
}
