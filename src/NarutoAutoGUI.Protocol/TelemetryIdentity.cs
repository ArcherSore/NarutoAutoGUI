using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NarutoAutoGUI.Protocol;

// The anonymous user both the GUI and the Worker report as: a salted hash of the Windows MachineGuid, the same in
// both processes and stable across restarts, but never the machine's own name or identifier.
[SupportedOSPlatform("windows")]
public static class TelemetryIdentity
{
    private static readonly Lazy<string> UserId = new(() => {
        string? machine = null;
        try {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machine = key?.GetValue("MachineGuid") as string;
        } catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
            or System.Security.SecurityException) {
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            "naruto-telemetry-v1:" + (string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    });

    public static string AnonymousUserId() => UserId.Value;
}
