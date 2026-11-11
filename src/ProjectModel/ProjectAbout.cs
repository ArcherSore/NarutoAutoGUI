using System.Text.RegularExpressions;

namespace ProjectModel;

public sealed record ProjectText(string? Markdown = null, Uri? Url = null, string? Error = null);

public sealed record ProjectAbout(
    string Name, ProjectText? Description, ProjectText? Contact, ProjectText? License, Uri? Github);

// Resolves like welcome, except a URL stays a link and an explicit file that cannot be read reports so.
internal static partial class ProjectAboutResolver
{
    internal static ProjectAbout Read(string projectRoot, string name, string? description, string? contact,
        string? license, string? github) => new(name, ReadText(projectRoot, description),
        ReadText(projectRoot, contact), ReadText(projectRoot, license), Repository(github));

    private static ProjectText? ReadText(string projectRoot, string? source)
    {
        if (ProjectContent.KeyText(source) is not { } text) { return null; }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") {
            return new(Url: uri);
        }
        var reference = ProjectContent.Classify(text);
        if (reference == FileReference.None) { return new(Markdown: text); }
        try {
            var content = ProjectContent.ReadFile(projectRoot, text);
            return string.IsNullOrWhiteSpace(content) ? null : new(Markdown: content);
        } catch (Exception) when (reference == FileReference.Possible) {
            return new(Markdown: text);
        } catch (Exception) {
            return new(Error: $"无法读取项目文件“{text}”。");
        }
    }

    // Keep in step with UpdateEngine/src/lib.rs::repository (including the default HTTPS port).
    private static Uri? Repository(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            // System.Uri decodes escaped ASCII in paths, while the Engine rejects it as an invalid repository name.
            || value.Split('/', '\\').Any(part => part.Contains('%') && !EncodedDot().IsMatch(part))) {
            return null;
        }
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 && OwnerName().IsMatch(parts[0]) && RepositoryName().IsMatch(parts[1])
            && parts[1] is not ("." or "..") ? uri : null;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z")]
    private static partial Regex OwnerName();

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+\z")]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"\A(?:\.|%2e){1,2}\z", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedDot();
}
