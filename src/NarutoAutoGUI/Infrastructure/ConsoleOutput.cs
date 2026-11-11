using System.Runtime.InteropServices;
using System.Text;

namespace NarutoAutoGUI.Infrastructure;

// A Windows GUI program has no console of its own. Text meant for a terminal goes to redirected output when there is
// one, and otherwise to the console of the process that started the program.
internal static class ConsoleOutput
{
    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private static readonly IntPtr NoHandle = new(-1);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsole(IntPtr console, string text, int length, out int written, IntPtr reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] bytes, int length, out int written, IntPtr overlapped);

    internal static void Write(string text)
    {
        text = text.ReplaceLineEndings();
        var output = GetStdHandle(StdOutputHandle);
        if (output != IntPtr.Zero && output != NoHandle) {
            WriteTo(output, text);
            return;
        }
        if (!AttachConsole(AttachParentProcess)) {
            return;
        }
        try {
            var console = CreateFile(
                "CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (console != NoHandle) {
                try {
                    WriteTo(console, text);
                } finally {
                    CloseHandle(console);
                }
            }
        } finally {
            FreeConsole();
        }
    }

    private static void WriteTo(IntPtr handle, string text)
    {
        if (GetConsoleMode(handle, out _)) {
            WriteConsole(handle, text, text.Length, out _, IntPtr.Zero);
            return;
        }
        var bytes = new UTF8Encoding(false).GetBytes(text);
        WriteFile(handle, bytes, bytes.Length, out _, IntPtr.Zero);
    }
}
