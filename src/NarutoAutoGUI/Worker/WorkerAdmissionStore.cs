using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoGUI.Worker;

internal sealed record WorkerAdmissionRecord(
    Guid WorkerInstanceId,
    string LaunchToken,
    uint ChildSessionId,
    int? WorkerPid,
    string RuntimeProfileDigest,
    DateTime CreatedAtUtc);

internal sealed class WorkerAdmissionStore
{
    private readonly string _stateDirectory;
    private readonly string _launchDirectory;
    private readonly string _recordPath;

    internal WorkerAdmissionStore(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _launchDirectory = Path.Combine(_stateDirectory, "launch");
        _recordPath = Path.Combine(_stateDirectory, "worker.json");
    }

    internal string GetManifestPath(Guid workerInstanceId) =>
        Path.Combine(_launchDirectory, $"{workerInstanceId:N}.json");

    internal WorkerAdmissionRecord? Load()
    {
        if (!File.Exists(_recordPath))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(_recordPath);
        return JsonSerializer.Deserialize<WorkerAdmissionRecord>(bytes, ProtocolJson.Options)
               ?? throw new InvalidDataException("Worker Admission Record 为空。 ");
    }

    internal void SaveManifest(LaunchManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJson.Options);
        if (bytes.Length > ProtocolConstants.MaximumLaunchManifestBytes)
        {
            throw new InvalidDataException(
                $"Launch Manifest 超过 {ProtocolConstants.MaximumLaunchManifestBytes} bytes。 ");
        }
        WriteAtomic(GetManifestPath(manifest.WorkerInstanceId), bytes);
    }

    internal void SaveRecord(WorkerAdmissionRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJson.Options);
        WriteAtomic(_recordPath, bytes);
    }

    internal void DeleteManifest(Guid workerInstanceId)
    {
        var path = GetManifestPath(workerInstanceId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal void DeleteRecord()
    {
        if (File.Exists(_recordPath))
        {
            File.Delete(_recordPath);
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("状态文件路径没有父目录。 ");
        EnsurePrivateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ApplyPrivateFileAcl(tempPath);
            File.Move(tempPath, path, overwrite: true);
            ApplyPrivateFileAcl(path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var sid = WindowsIdentity.GetCurrent().User
                  ?? throw new InvalidOperationException("无法取得当前用户 SID。 ");
        var security = new DirectorySecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ApplyPrivateFileAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var sid = WindowsIdentity.GetCurrent().User
                  ?? throw new InvalidOperationException("无法取得当前用户 SID。 ");
        var security = new FileSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
