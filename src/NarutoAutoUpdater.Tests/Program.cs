using NarutoAutoGUI.Updates;
using System.Net;
using System.Text;
using System.IO.Compression;

if (SemanticVersion.Parse("v2.10.0").CompareTo(SemanticVersion.Parse("2.9.0")) <= 0) {
    throw new Exception("SemVer numeric precedence failed.");
}
Console.WriteLine("UPDATE TEST PASS: SemVer numeric precedence.");
foreach (var (left, right, expected) in new[] {
    ("v1.0.0", "1.0.0+build.4", 0), ("1.0.0-alpha.9", "1.0.0-alpha.10", -1),
    ("1.0.0-alpha", "1.0.0", -1), ("1.0.0-1", "1.0.0-alpha", -1),
    ("1.0.0--1", "1.0.0-1", 1), ("4294967296.0.0", "2.0.0", 1),
    ("1.0.0-4294967296", "1.0.0-4294967297", -1)
}) {
    if (Math.Sign(SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right))) != expected) {
        throw new Exception($"SemVer precedence: {left} vs {right}");
    }
}
foreach (var invalid in new[] {
    "2.1", "2.01.0", "2.1.0-01", "2.1.0\n", " 2.1.0"
}) {
    ExpectFailure<InvalidDataException>(() => SemanticVersion.Parse(invalid));
}
const string releaseJson = """
    {"tag_name":"v2.10.0","draft":false,"prerelease":false,"body":"Release notes","assets":[
    {"name":"MaaNOP-linux-x86_64-v2.10.0.zip"},
    {"name":"MaaNOP-win-x86_64-v2.10.0.zip","size":3,
    "browser_download_url":"https://github.com/owner/repo/releases/download/v2.10.0/pkg.zip",
    "digest":"sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"}]}
    """;
var release = UpdateRelease.Parse(releaseJson, "v2.9.0");
if (release?.Tag != "v2.10.0" || release.Notes != "Release notes") {
    throw new Exception("Stable Windows x64 asset selection failed.");
}
foreach (var json in new[] { releaseJson.Replace("\"draft\":false", "\"draft\":true"),
    releaseJson.Replace("\"prerelease\":false", "\"prerelease\":true") }) {
    if (UpdateRelease.Parse(json, "v2.9.0") is not null) {
        throw new Exception("Non-stable release accepted.");
    }
}
if (UpdateRelease.Parse(releaseJson, "2.10.0+local") is not null
    || UpdateRelease.Parse(releaseJson, "3.0.0") is not null) {
    throw new Exception("Equal or older release accepted.");
}
ExpectFailure<InvalidDataException>(() => UpdateRelease.Parse(
    releaseJson.Replace("MaaNOP-win-x86_64-", "MaaNOP-win-arm64-"), "2.9.0"));
ExpectFailure<InvalidDataException>(() => UpdateRelease.Parse(releaseJson.Replace("sha256:", "sha1:"), "2.9.0"));
var testRoot = Path.Combine(Path.GetTempPath(), $"NarutoUpdateTest-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
try {
    var completionDirectory = UpdateStorage.DirectoryFor(testRoot);
    var completionPath = Path.Combine(completionDirectory, "completed.json");
    try {
        var completed = new PreparedUpdate(testRoot, "", "v2.10.0", "Release notes");
        UpdateStorage.SaveCompletion(completed);
        ExpectFailure<InvalidDataException>(() => UpdateStorage.TakeCompletion(testRoot, "invalid"));
        if (UpdateStorage.TakeCompletion(testRoot, "2.10.0")?.Notes != completed.Notes
            || UpdateStorage.TakeCompletion(testRoot, "2.10.0") is not null) {
            throw new Exception("Completion was lost on parse failure or consumed more than once.");
        }
        UpdateStorage.SaveCompletion(completed with { Tag = "invalid" });
        ExpectFailure<InvalidDataException>(() => UpdateStorage.TakeCompletion(testRoot, "2.10.0"));
        if (!File.Exists(completionPath)) {
            throw new Exception("Invalid completion was deleted before validation.");
        }
    } finally {
        File.Delete(completionPath);
        Directory.Delete(completionDirectory);
    }
    using var client = new HttpClient(new ResponseHandler("abc"));
    var service = new UpdateService(client, _ => { });
    var zip = Path.Combine(testRoot, "download.zip");
    await service.DownloadAsync(release, zip, null, CancellationToken.None);
    if (File.ReadAllText(zip) != "abc") {
        throw new Exception("Download did not preserve bytes.");
    }
    File.Delete(zip);
    using (var cancelled = new CancellationTokenSource()) {
        cancelled.Cancel();
        try {
            await service.DownloadAsync(release, zip, null, cancelled.Token);
            throw new Exception("Cancelled download accepted.");
        } catch (OperationCanceledException) {
            if (File.Exists(zip)) {
                throw new Exception("Cancelled partial download retained.");
            }
        }
    }
    try {
        await service.DownloadAsync(release with { Sha256 = new string('0', 64) }, zip, null, default);
        throw new Exception("Corrupt download accepted.");
    } catch (InvalidDataException) {
        if (File.Exists(zip)) {
            throw new Exception("Corrupt download retained.");
        }
    }
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) {
        using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
        writer.Write("escape");
    }
    var staging = Path.Combine(testRoot, "staging");
    try {
        UpdatePackage.Extract(zip, staging, release.Tag, default);
        throw new Exception("Traversal ZIP accepted.");
    } catch (InvalidDataException) {
        if (File.Exists(Path.Combine(testRoot, "escaped.txt"))) {
            throw new Exception("ZIP escaped staging.");
        }
    }
    var install = Path.Combine(testRoot, "MaaNOP");
    var next = install + ".update-" + Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(Path.Combine(install, "config", "plans"));
    Directory.CreateDirectory(Path.Combine(install, "logs"));
    File.WriteAllText(Path.Combine(install, "config", "plans", "mine.json"), "user-owned");
    File.WriteAllText(Path.Combine(install, "logs", "old.log"), "old log");
    File.WriteAllText(Path.Combine(install, "obsolete.dll"), "old");
    CreatePackage(next);
    new InstallTransaction(_ => { }).Install(install, next, "v2.10.0");
    if (File.ReadAllText(Path.Combine(install, "config", "plans", "mine.json")) != "user-owned"
        || File.ReadAllText(Path.Combine(install, "logs", "old.log")) != "old log"
        || File.Exists(Path.Combine(install, "obsolete.dll")) || !Directory.Exists(install + ".old")) {
        throw new Exception("Full replacement did not preserve user data / remove obsolete files.");
    }
    next = install + ".update-" + Guid.NewGuid().ToString("N");
    CreatePackage(next);
    Directory.CreateDirectory(Path.Combine(next, "config"));
    File.WriteAllText(Path.Combine(next, "config", "defaults.json"), "must not merge");
    new InstallTransaction(_ => { }).Install(install, next, "v2.10.0");
    if (File.Exists(Path.Combine(install, "config", "defaults.json"))
        || Directory.GetDirectories(testRoot, "*.old*").Length != 1) {
        throw new Exception("Backup bound or user-owned config violated.");
    }
    next = install + ".update-" + Guid.NewGuid().ToString("N");
    CreatePackage(next);
    var moves = 0;
    var transaction = new InstallTransaction(_ => { }, (from, to) =>
    {
        if (++moves == 2) {
            throw new IOException("Injected second rename failure.");
        }
        Directory.Move(from, to);
    });
    ExpectFailure<IOException>(() => transaction.Install(install, next, "v2.10.0"));
    if (!File.Exists(Path.Combine(install, "config", "plans", "mine.json")) || Directory.Exists(install + ".old")) {
        throw new Exception("Rollback did not restore the old path.");
    }
    ExpectFailure<InvalidDataException>(() => UpdatePackage.Validate(next, "v3.0.0"));
    foreach (var entry in new[] { "/absolute", "C:/escape", "a/../../escape", "a\\..\\..\\escape",
        "CON", "a.txt:stream", "trailing. ", "//server/share/file" }) {
        File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("bad");
        }
        ExpectFailure<InvalidDataException>(() => UpdatePackage.Extract(zip, staging, release.Tag, default));
    }
    using (var locked = File.Open(Path.Combine(install, "NarutoAutoGUI.exe"), FileMode.Open,
        FileAccess.Read, FileShare.None)) {
        ExpectFailure<IOException>(() => new InstallTransaction(_ => { }).Install(install, next, "v2.10.0"));
        if (!Directory.Exists(install)) {
            throw new Exception("Locked installation was moved.");
        }
    }
    moves = 0;
    transaction = new InstallTransaction(_ => { }, (from, to) =>
    {
        if (++moves >= 2) {
            throw new IOException("Injected swap and rollback failure.");
        }
        Directory.Move(from, to);
    });
    ExpectFailure<AggregateException>(() => transaction.Install(install, next, "v2.10.0"));
    if (!File.Exists(Path.Combine(install + ".old", "config", "plans", "mine.json"))) {
        throw new Exception("Rollback failure lost the recoverable backup.");
    }
} finally {
    Directory.Delete(testRoot, true);
}
Console.WriteLine("UPDATE TEST PASS: release selection, download/SHA256, ZIP paths, preservation, swap/rollback.");

static void CreatePackage(string directory)
{
    foreach (var file in UpdatePackage.RequiredFiles) {
        var target = Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "fixture");
    }
    Directory.CreateDirectory(Path.Combine(directory, "resource"));
    Directory.CreateDirectory(Path.Combine(directory, "agent"));
    File.WriteAllText(Path.Combine(directory, "interface.json"), """{"name":"MaaNOP","version":"v2.10.0"}""");
}

static void ExpectFailure<T>(Action action) where T : Exception
{
    try {
        action();
    } catch (T) {
        return;
    }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class ResponseHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        });
    }
}
