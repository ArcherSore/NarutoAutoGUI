using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NarutoAutoGUI.ChildSession;

internal static class ShellCredentialAccess
{
    internal static void Run(Action action)
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0) {
            throw new InvalidOperationException("当前会话的资源管理器不可用，无法访问保存的 Windows 凭据。");
        }
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!OpenProcessToken(process, 0xA, out var token)) { // TOKEN_QUERY | TOKEN_DUPLICATE
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using (token) {
            using var current = WindowsIdentity.GetCurrent();
            using var shell = new WindowsIdentity(token.DangerousGetHandle());
            using var currentProcess = Process.GetCurrentProcess();
            if (!Matches(current.User, currentProcess.SessionId, shell.User,
                    TokenNumber(token, 12), TokenNumber(token, 20) != 0)) {
                throw new InvalidOperationException("资源管理器的用户、会话或权限不匹配，无法访问 Windows 凭据。");
            }
            WindowsIdentity.RunImpersonated(token, action);
        }
    }

    internal static bool Matches(SecurityIdentifier? currentUser, int currentSession,
        SecurityIdentifier? shellUser, int shellSession, bool shellElevated) =>
        currentUser is not null && shellUser == currentUser && shellSession == currentSession && !shellElevated;

    private static int TokenNumber(SafeAccessTokenHandle token, int informationClass)
    {
        if (!GetTokenInformation(token, informationClass, out var value, sizeof(int), out _)) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return value;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access,
        out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int value, int size, out int required);
}
