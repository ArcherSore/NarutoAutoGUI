using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace NarutoAutoGUI.ChildSession;

// Native helpers adapted (trimmed) from BetterGI 0.63.0 ChildSessionNativeMethods.cs.
// Edition-agnostic surface only: enable / query / logoff Child Session + cross-session
// process lookup for verification. Per MS Child Sessions docs, Child Session is a special
// LOOPBACK Remote Desktop session supported on Windows 8+ (excluded only: Windows RT,
// Server 2012 Server Core, Hyper-V Server 2012). It does NOT require the Remote Interactive
// right / RDP host to be enabled, so no fDenyTSConnections / RDP-host / TermService-restart
// logic belongs here. No user32 "Input Capture Window" focus helpers (only for shortcuts).
internal static class ChildSessionNativeMethods
{
    private const int DefaultRdpPort = 3389;
    private const int ErrorNotFound = 1168;
    private const string RdpTcpRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string TermServiceParametersRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    private const string RdpWrapperLibraryName = "rdpwrap.dll";
    private const uint NoChildSessionId = uint.MaxValue;
    private static readonly IntPtr CurrentServerHandle = IntPtr.Zero;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr serverHandle, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    // Returns false when WTS reports no Child Session, either as a successful ULONG(-1)
    // result or as ERROR_NOT_FOUND on Windows builds that use that native result shape.
    // All other native call failures retain their Win32 error code and are thrown.
    internal static bool TryGetChildSessionId(out uint childSessionId)
    {
        if (!WTSGetChildSessionId(out childSessionId)) {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) {
                childSessionId = NoChildSessionId;
                return false;
            }

            throw CreateLastWin32Exception("无法取得 RDP Child Session ID");
        }

        return childSessionId != NoChildSessionId;
    }

    internal static uint? TryGetChildSessionId()
    {
        return TryGetChildSessionId(out var sessionId) ? sessionId : null;
    }

    internal static bool IsChildSessionsEnabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled)) {
            throw CreateLastWin32Exception("无法读取 RDP Child Session 状态");
        }

        return enabled;
    }

    // WTSEnableChildSessions requires the caller to be a member of the Administrators group.
    // Failure is reported with the concrete Win32 error code; we never pre-reject by edition.
    internal static void EnableChildSessions()
    {
        if (!WTSEnableChildSessions(true)) {
            throw CreateLastWin32Exception("无法启用 RDP Child Session");
        }
    }

    internal static uint? TerminateChildSession() => TerminateChildSession(wait: true);

    internal static uint? TerminateChildSession(bool wait)
    {
        if (!WTSGetChildSessionId(out var childSessionId)) {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) {
                return null;
            }

            throw CreateLastWin32Exception("无法取得 RDP Child Session ID");
        }

        if (childSessionId == NoChildSessionId) {
            return null;
        }

        if (!WTSLogoffSession(CurrentServerHandle, childSessionId, wait)) {
            throw CreateLastWin32Exception($"无法注销 Child Session {childSessionId}");
        }

        return childSessionId;
    }

    // Read the configured RDP-Tcp port (default 3389). Informational: fed to AdvancedSettings7.RDPPort.
    internal static int GetConfiguredRdpPort()
    {
        try {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var rdpTcpKey = localMachine.OpenSubKey(RdpTcpRegistryPath);
            var configuredPort = rdpTcpKey?.GetValue("PortNumber");
            return configuredPort is int port and > 0 and <= ushort.MaxValue
                ? port
                : DefaultRdpPort;
        } catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException) {
            return DefaultRdpPort;
        }
    }

    // Informational only. This PoC does NOT install or depend on RDP Wrapper.
    internal static bool IsRdpWrapperEnabled()
    {
        try {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var termServiceParametersKey =
                localMachine.OpenSubKey(TermServiceParametersRegistryPath);
            var serviceLibraryPath =
                termServiceParametersKey?.GetValue("ServiceDll") as string;
            return serviceLibraryPath?.Contains(RdpWrapperLibraryName, StringComparison.OrdinalIgnoreCase) == true;
        } catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException) {
            return false;
        }
    }

    // Enumerate processes across all sessions without decoding a native WTS_PROCESS_INFO buffer.
    // WMI returns managed property values, so this code does not depend on the native entry layout
    // or pointer encoding used by a particular Windows build. If WMI is unavailable/disabled, use
    // System.Diagnostics as a safe managed fallback; verification is diagnostic and must never
    // crash the launcher or tear down an established Child Session.
    internal static List<(uint ProcessId, uint SessionId, string Name)> EnumerateProcesses()
    {
        try {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, SessionId, Name FROM Win32_Process");
            using var results = searcher.Get();
            var list = new List<(uint, uint, string)>(results.Count);

            foreach (ManagementObject process in results) {
                using (process) {
                    if (process["ProcessId"] is not uint processId || process["SessionId"] is not uint sessionId) {
                        continue;
                    }

                    var name = process["Name"] as string ?? string.Empty;
                    list.Add((processId, sessionId, name));
                }
            }

            return list;
        } catch (Exception exception) when (exception is ManagementException or COMException
            or UnauthorizedAccessException or InvalidOperationException) {
            var list = new List<(uint, uint, string)>();
            foreach (var process in Process.GetProcesses()) {
                using (process) {
                    try {
                        list.Add(((uint)process.Id, (uint)process.SessionId, process.ProcessName + ".exe"));
                    } catch (Exception processException) when (processException is Win32Exception
                        or InvalidOperationException or NotSupportedException) {
                        // A process may exit or become inaccessible between enumeration and read.
                    }
                }
            }

            return list;
        }
    }

    // Find the first process matching processName running inside sessionId.
    // processName is matched case-insensitively and should include the extension (e.g. "Launch.exe").
    internal static bool TryFindProcessInSession(string processName, uint sessionId, out uint processId)
    {
        processId = 0;
        foreach (var (pid, sid, name) in EnumerateProcesses()) {
            if (sid == sessionId && string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) {
                processId = pid;
                return true;
            }
        }

        return false;
    }

    private static Win32Exception CreateLastWin32Exception(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation}（Win32 错误 {error}）");
    }
}
