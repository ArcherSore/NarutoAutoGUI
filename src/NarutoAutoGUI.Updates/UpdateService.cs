using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NarutoAutoGUI.Updates;

public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond, bool Verifying = false);

public sealed class UpdateService(HttpClient client, Action<string> log)
{
    public async Task<UpdateRelease?> CheckAsync(UpdateSource source, CancellationToken cancellation)
    {
        log($"开始检查更新；本地版本={source.Version}；仓库={source.Repository}");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{source.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("NarutoAutoGUI-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellation);
        using var document = JsonDocument.Parse(json);
        log($"远程版本={document.RootElement.GetProperty("tag_name").GetString()}");
        var release = UpdateRelease.Parse(json, source.Version);
        log(release is null ? "Release 解析完成：无较新正式版本。"
            : $"远程版本={release.Tag}；目标 asset={release.Name}；大小={release.Size}");
        return release;
    }

    public async Task DownloadAsync(
        UpdateRelease release, string destination,
        IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        log($"开始下载：{release.Name}");
        if (File.Exists(destination)) {
            throw new IOException("下载目标已存在。");
        }
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("NarutoAutoGUI-Updater/1.0");
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) {
                throw new InvalidDataException("下载文件大小与 Release 不一致。");
            }
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation)) {
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous);
                var buffer = new byte[81920];
                long received = 0;
                long previous = 0;
                var tick = Stopwatch.GetTimestamp();
                while (true) {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, timeout.Token);
                    if (count == 0) {
                        break;
                    }
                    received += count;
                    if (received > release.Size) {
                        throw new InvalidDataException("下载文件大于 Release 声明大小。");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    var elapsed = Stopwatch.GetElapsedTime(tick).TotalSeconds;
                    if (elapsed >= 0.2 || received == release.Size) {
                        progress?.Report(new DownloadProgress(received, release.Size,
                            (received - previous) / Math.Max(elapsed, 0.001)));
                        previous = received;
                        tick = Stopwatch.GetTimestamp();
                    }
                }
                if (received != release.Size) {
                    throw new InvalidDataException("下载未完成，请重新下载。");
                }
            }
            log("下载完成，开始 SHA256 校验。");
            progress?.Report(new DownloadProgress(release.Size, release.Size, 0, true));
            await using var file = File.OpenRead(destination);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("下载文件校验失败，请重新下载。");
            }
            log("SHA256 校验成功。");
        } catch {
            if (File.Exists(destination)) {
                File.Delete(destination);
            }
            throw;
        }
    }
}
