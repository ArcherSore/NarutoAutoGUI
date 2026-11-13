using Microsoft.Win32;

namespace Gui.ChildSession;

// Read-only checks that explain a failure; they never change Windows settings or decide whether to connect.
internal static class ChildSessionEnvironment
{
    private const string PasswordLessKey =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

    // The Child Session signs in with the account password, which this Windows setting can refuse.
    internal static bool RequiresWindowsHelloSignIn() =>
        Registry.GetValue(PasswordLessKey, "DevicePasswordLessBuildVersion", 0) is 2;
}
