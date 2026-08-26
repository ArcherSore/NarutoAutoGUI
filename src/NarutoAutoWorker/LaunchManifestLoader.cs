using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal static class LaunchManifestLoader
{
    internal static LaunchManifest Load(WorkerArguments arguments)
    {
        var info = new FileInfo(arguments.ManifestPath);
        if (!info.Exists) {
            throw new FileNotFoundException("Launch Manifest 不存在。", arguments.ManifestPath);
        }
        if (info.Length is <= 0 or > ProtocolConstants.MaximumLaunchManifestBytes) {
            throw new InvalidDataException(
                $"Launch Manifest 大小非法：{info.Length} bytes。 ");
        }

        var bytes = File.ReadAllBytes(arguments.ManifestPath);
        var manifest = JsonSerializer.Deserialize<LaunchManifest>(bytes, ProtocolJson.Options)
                       ?? throw new InvalidDataException("Launch Manifest 为空。 ");
        if (manifest.LaunchContextVersion != ProtocolConstants.LaunchContextVersion) {
            throw new InvalidDataException(
                $"不支持 launchContextVersion={manifest.LaunchContextVersion}。 ");
        }
        if (manifest.WorkerInstanceId != arguments.WorkerInstanceId) {
            throw new InvalidDataException("Manifest workerInstanceId 与启动参数不一致。 ");
        }
        if (manifest.Resources.Count != 1) {
            throw new InvalidDataException("首版 Worker 要求恰好一个 resource。 ");
        }

        _ = PathCanonicalizerV1.Canonicalize(manifest.ProjectRoot);
        _ = PathCanonicalizerV1.Canonicalize(manifest.Agent.WorkingDirectory);
        foreach (var path in manifest.Resources.SelectMany(resource => resource.Paths)) {
            _ = PathCanonicalizerV1.Canonicalize(path);
        }
        CanonicalDigest.ValidateDigestFormat(manifest.RuntimeProfileDigest, nameof(manifest.RuntimeProfileDigest));
        var actualDigest = CanonicalDigest.ComputeRuntimeProfileDigestV1(
            manifest.ProjectRoot, manifest.Controller, manifest.Resources, manifest.Agent);
        if (!string.Equals(actualDigest, manifest.RuntimeProfileDigest, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Runtime Profile Digest 不一致：manifest={manifest.RuntimeProfileDigest}，actual={actualDigest}。 ");
        }

        return manifest;
    }
}
