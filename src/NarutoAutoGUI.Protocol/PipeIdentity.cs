using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace NarutoAutoGUI.Protocol;

public static class PipeIdentity
{
    private const string Prefix = "NarutoAutoGUI.Worker.v1";

    public static string ForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NarutoAutoGUI Worker IPC 只支持 Windows。 ");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
                  ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。 ");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))
            .ToLowerInvariant();
        return $"{Prefix}.{hash[..24]}";
    }
}
