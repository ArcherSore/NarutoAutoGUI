namespace NarutoAutoWorker;

internal sealed record WorkerArguments(Guid WorkerInstanceId, string LaunchToken, string ManifestPath)
{
    internal static WorkerArguments Parse(string[] args)
    {
        string? instance = null;
        string? token = null;
        string? manifest = null;
        for (var index = 0; index < args.Length; index++) {
            var name = args[index];
            if (index + 1 >= args.Length) {
                throw new ArgumentException($"启动参数 {name} 缺少值。 ");
            }

            switch (name) {
                case "--instance":
                    instance = args[++index];
                    break;
                case "--token":
                    token = args[++index];
                    break;
                case "--manifest":
                    manifest = args[++index];
                    break;
                default:
                    throw new ArgumentException($"未知启动参数：{name}。 ");
            }
        }

        if (!Guid.TryParse(instance, out var workerInstanceId)) {
            throw new ArgumentException("--instance 必须是 GUID。 ");
        }
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32) {
            throw new ArgumentException("--token 缺失或过短。 ");
        }
        if (string.IsNullOrWhiteSpace(manifest) || !Path.IsPathFullyQualified(manifest)) {
            throw new ArgumentException("--manifest 必须是绝对路径。 ");
        }

        return new WorkerArguments(workerInstanceId, token, Path.GetFullPath(manifest));
    }
}
