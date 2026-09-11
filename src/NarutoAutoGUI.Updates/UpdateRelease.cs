using System.Text.Json;
using System.Text.RegularExpressions;

namespace NarutoAutoGUI.Updates;

public sealed record UpdateSource(string Version, string Repository)
{
    public static UpdateSource Load(string directory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "interface.json")));
        var root = json.RootElement;
        if (root.GetProperty("name").GetString() != "MaaNOP") {
            throw new InvalidDataException("当前项目不是 MaaNOP。");
        }
        var version = root.GetProperty("version").GetString()!;
        _ = SemanticVersion.Parse(version);
        var github = root.GetProperty("github").GetString();
        if (!Uri.TryCreate(github, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) {
            throw new InvalidDataException("Project Interface 未提供有效的 GitHub 更新仓库。");
        }
        var repository = uri.AbsolutePath.Trim('/');
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$")) {
            throw new InvalidDataException("GitHub 更新仓库地址无效。");
        }
        return new UpdateSource(version, repository);
    }
}

public sealed record UpdateRelease(string Tag, string Notes, string Name, Uri DownloadUrl, long Size, string Sha256)
{
    public static UpdateRelease? Parse(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) {
            return null;
        }
        var tag = root.GetProperty("tag_name").GetString()!;
        if (SemanticVersion.Parse(tag).CompareTo(SemanticVersion.Parse(currentVersion)) <= 0) {
            return null;
        }
        var name = $"MaaNOP-win-x86_64-{tag}.zip";
        var assets = root.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) {
            throw new InvalidDataException("当前 Release 不包含唯一兼容的 Windows x64 更新包。");
        }
        var asset = assets[0];
        var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (digest is null || !Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}\z")) {
            throw new InvalidDataException("更新包缺少有效的 SHA256 摘要。");
        }
        if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url)
            || url.Scheme != "https" || url.UserInfo.Length != 0) {
            throw new InvalidDataException("更新包下载地址无效。");
        }
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0) {
            throw new InvalidDataException("更新包大小无效。");
        }
        return new UpdateRelease(tag, root.GetProperty("body").GetString() ?? "", name, url, size, digest[7..]);
    }
}
