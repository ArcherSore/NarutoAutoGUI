using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NarutoAutoGUI.ProjectModel;

public sealed record ProjectWelcome(string Title, IReadOnlyList<string> Contents, string Hash);

// Follows MXU's welcome rules, without translations or URLs: a `$` key shows as its name.
internal static partial class ProjectWelcomeResolver
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
        var title = StripKeyMarker(project.Label ?? string.Empty);
        return new ProjectWelcome(title.Length > 0 ? title : project.Provenance.Name, contents, ComputeHash(entries));
    }

    // A single announcement is keyed by its content alone, so renaming its file does not prompt again.
    private static string ComputeHash(Entry[] entries) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(entries.Length == 1 ? entries[0].Content : JsonSerializer.Serialize(entries))));

    private static string ReadContent(string source, string projectRoot, Action<string> warn)
    {
        var text = StripKeyMarker(source);
        if (!IsFilePath(text)) {
            return text;
        }
        try {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, text));
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? File.ReadAllText(path)
                : throw new IOException("路径超出项目目录。");
        } catch (Exception exception) {
            // Whether a string names a file is a guess, so one that cannot be read is shown as the text it may be.
            warn($"读取公告文件失败，已按文本显示。source={text}；{exception.Message}");
            return text;
        }
    }

    private static string StripKeyMarker(string text) => text.StartsWith('$') ? text[1..] : text;

    private static bool IsFilePath(string text)
    {
        if (text.StartsWith("http://", StringComparison.Ordinal)
            || text.StartsWith("https://", StringComparison.Ordinal)) {
            return false;
        }
        if (text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith("../", StringComparison.Ordinal)
            || DocumentExtension().IsMatch(text)) {
            return true;
        }
        if (text.Length > 100 || !SimpleName().IsMatch(text) || text.All(char.IsAsciiDigit)) {
            return false;
        }
        return UpperCaseName().IsMatch(text) || text.Contains('/') || text.Contains('\\');
    }

    [GeneratedRegex(@"\.(md|txt|json|html|htm)\z", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentExtension();

    [GeneratedRegex(@"\A[A-Za-z0-9_\-./\\]+\z")]
    private static partial Regex SimpleName();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_\-]*\z")]
    private static partial Regex UpperCaseName();
}
