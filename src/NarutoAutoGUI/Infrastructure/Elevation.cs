using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace NarutoAutoGUI.Infrastructure;

// The program needs administrator rights (Child Session, Task Scheduler) but declares none in its manifest, so that
// it can answer --help before any UAC prompt. A normal launch asks for them here instead.
internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Starts this program again with the same arguments, elevated. False when the user declined the prompt.
    internal static bool TryRelaunch(IReadOnlyList<string> arguments)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得程序路径。");
        var start = new ProcessStartInfo(path, arguments) {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
        };
        try {
            Process.Start(start)?.Dispose();
            return true;
        } catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled) {
            return false;
        }
    }
}
