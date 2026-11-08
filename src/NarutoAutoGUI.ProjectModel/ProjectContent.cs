using System.Text.RegularExpressions;

namespace NarutoAutoGUI.ProjectModel;

internal enum FileReference { None, Possible, Explicit }

// MXU's rules for Project Interface text that may name a file in the project directory.
internal static partial class ProjectContent
{
    internal static string StripKeyMarker(string text) => text.StartsWith('$') ? text[1..] : text;

    // A bare upper-case word such as LICENSE may name a file, or may just be text such as MIT.
    internal static FileReference Classify(string text)
    {
        if (text.StartsWith("http://", StringComparison.Ordinal)
            || text.StartsWith("https://", StringComparison.Ordinal)) {
            return FileReference.None;
        }
        if (text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith("../", StringComparison.Ordinal)
            || DocumentExtension().IsMatch(text)) {
            return FileReference.Explicit;
        }
        if (text.Length > 100 || !SimpleName().IsMatch(text) || text.All(char.IsAsciiDigit)) {
            return FileReference.None;
        }
        return text.Contains('/') || text.Contains('\\') ? FileReference.Explicit
            : UpperCaseName().IsMatch(text) ? FileReference.Possible : FileReference.None;
    }

    internal static string ReadFile(string projectRoot, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? File.ReadAllText(path)
            : throw new IOException("路径超出项目目录。");
    }

    [GeneratedRegex(@"\.(md|txt|json|html|htm)\z", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentExtension();

    [GeneratedRegex(@"\A[A-Za-z0-9_\-./\\]+\z")]
    private static partial Regex SimpleName();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_\-]*\z")]
    private static partial Regex UpperCaseName();
}
