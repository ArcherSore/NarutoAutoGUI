using System.Diagnostics;

namespace NarutoAutoGUI.Infrastructure;

// The logon task also fires for the Child Session this program creates, because the same account signs in there. A
// launch by the system must not start a second copy in that desktop, so it yields to a copy running in any session.
internal static class RunningCopies
{
    internal static bool Exists(string ownPath, int ownId, IEnumerable<(int Id, string? Path)> processes) =>
        processes.Any(process => process.Id != ownId
            && string.Equals(process.Path, ownPath, StringComparison.OrdinalIgnoreCase));

    internal static bool Exists()
    {
        using var current = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(current.ProcessName);
        try {
            return Exists(Environment.ProcessPath ?? string.Empty, current.Id,
                others.Select(process => (process.Id, PathOf(process))));
        } finally {
            foreach (var process in others) {
                process.Dispose();
            }
        }
    }

    private static string? PathOf(Process process)
    {
        try {
            return process.MainModule?.FileName;
        } catch (Exception) {
            return null;
        }
    }
}
