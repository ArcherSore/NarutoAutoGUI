using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Protocol;

// Diagnostic only. Never launches/elevates a process or changes the production credential.
internal static class Program
{
    private const string SavedTarget = AppIdentity.CredentialTarget;

    private static int Main(string[] args)
    {
        if (args.Length != 1) {
            Console.Error.WriteLine("Usage: CredentialProbe <result.json>");
            return 1;
        }
        try {
            using var original = WindowsIdentity.GetCurrent();
            var before = Identity();
            var results = new List<object>();
            var direct = EnumerateSaved();
            results.Add(new { Mode = "direct", Identity = before, Read = direct, RoundTrip = RoundTrip() });
            foreach (var mode in new[] { "linked", "shell", "production-shell" }) {
                try {
                    using var token = mode == "linked" ? Linked(original.AccessToken) : Shell();
                    using var candidate = new WindowsIdentity(token.DangerousGetHandle());
                    if (candidate.User != original.User || Number(token, 12) != before.Session
                        || Number(token, 20) != 0) {
                        results.Add(new { Mode = mode, Rejected = "identity-session-or-elevation" });
                        continue;
                    }
                    Action<Action> run = mode == "production-shell"
                        ? Gui.ChildSession.ShellCredentialAccess.Run
                        : action => WindowsIdentity.RunImpersonated(token, action);
                    Observation? observation = null;
                    run(() => observation = new Observation(Identity(), EnumerateSaved(), RoundTrip()));
                    var restored = Identity() == before;
                    try {
                        run(() => { throw new ProbeException(); });
                    } catch (ProbeException) { }
                    var exceptionRestored = Identity() == before;
                    var after = EnumerateSaved();
                    var reproduced = before.Elevated && direct.Error == 1312 && after.Error == 1312;
                    results.Add(new {
                        Mode = mode, Observation = observation, Restored = restored,
                        ExceptionRestored = exceptionRestored, DirectAfter = after,
                        Confirmed1312Recovery = reproduced && observation!.Read.Error is 0 or 1168
                            && observation.RoundTrip.Passed && restored && exceptionRestored
                    });
                } catch (Exception exception) {
                    results.Add(new { Mode = mode, Error = Error(exception), Restored = Identity() == before });
                }
            }
            var report = new {
                Utc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
                Elevated = before.Elevated, Direct1312 = direct.Error == 1312, Results = results
            };
            File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(before.Elevated && direct.Error == 1312
                ? "Observed elevated 1312; inspect per-mode recovery results."
                : "INCONCLUSIVE: elevated 1312 was not reproduced; this is only a baseline.");
            return 0;
        } catch (Exception exception) {
            // Do not print exception messages: credential APIs must never expose their input.
            Console.Error.WriteLine($"Probe failed: code={Error(exception)}");
            return 1;
        }
    }

    private sealed class ProbeException : Exception
    {
    }
    private sealed record Context(int Session, bool Elevated, string AuthenticationId, bool Impersonating);
    private sealed record ReadResult(int Error, bool Found);
    private sealed record WriteResult(bool Passed, int Error, int CleanupError);
    private sealed record Observation(Context Identity, ReadResult Read, WriteResult RoundTrip);

    private static int Error(Exception exception) =>
        exception is Win32Exception native ? native.NativeErrorCode : exception.HResult;

    private static Context Identity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var impersonated = WindowsIdentity.GetCurrent(true);
        var statistics = Info(identity.AccessToken, 10);
        var authenticationId = Convert.ToHexString(statistics.AsSpan(8, 8));
        return new Context(Number(identity.AccessToken, 12), Number(identity.AccessToken, 20) != 0,
            authenticationId, impersonated is not null);
    }

    private static byte[] Info(SafeAccessTokenHandle token, int kind)
    {
        Native.GetTokenInformation(token, kind, IntPtr.Zero, 0, out var size);
        if (size <= 0) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var buffer = Marshal.AllocHGlobal(size);
        try {
            if (!Native.GetTokenInformation(token, kind, buffer, size, out _)) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var result = new byte[size];
            Marshal.Copy(buffer, result, 0, size);
            return result;
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int Number(SafeAccessTokenHandle token, int kind) => BitConverter.ToInt32(Info(token, kind));

    private static SafeAccessTokenHandle Linked(SafeAccessTokenHandle token) =>
        new(new IntPtr(BitConverter.ToInt64(Info(token, 19))));

    private static SafeAccessTokenHandle Shell()
    {
        var window = Native.GetShellWindow();
        if (window == IntPtr.Zero) {
            throw new Win32Exception(1168);
        }
        Native.GetWindowThreadProcessId(window, out var processId);
        using var process = Native.OpenProcess(0x1000, false, processId);
        if (process.IsInvalid || !Native.OpenProcessToken(process, 0xA, out var token)) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return token;
    }

    private static ReadResult EnumerateSaved()
    {
        if (!Native.CredEnumerate(SavedTarget, 0, out var count, out var credentials)) {
            return new ReadResult(Marshal.GetLastWin32Error(), false);
        }
        // The API supplies secrets, but the probe never reads their fields or serializes them.
        Native.CredFree(credentials);
        return new ReadResult(0, count > 0);
    }

    private static WriteResult RoundTrip()
    {
        var target = $"{AppIdentity.Name}/PermissionProbe/" + Guid.NewGuid().ToString("N");
        var error = 0;
        var cleanup = 0;
        var wrote = false;
        try {
            for (var revision = 0; revision < 2; revision++) {
                var secret = RandomNumberGenerator.GetBytes(32);
                var blob = Marshal.AllocHGlobal(secret.Length);
                try {
                    Marshal.Copy(secret, 0, blob, secret.Length);
                    var value = new Native.Credential {
                        Type = 1, TargetName = target, UserName = "permission-probe.invalid",
                        CredentialBlobSize = (uint)secret.Length, CredentialBlob = blob, Persist = 2
                    };
                    if (!Native.CredWrite(ref value, 0)) {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    wrote = true;
                    if (!Native.CredRead(target, 1, 0, out var saved)) {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    try {
                        var read = Marshal.PtrToStructure<Native.Credential>(saved);
                        var actual = new byte[read.CredentialBlobSize];
                        Marshal.Copy(read.CredentialBlob, actual, 0, actual.Length);
                        var matches = actual.AsSpan().SequenceEqual(secret);
                        CryptographicOperations.ZeroMemory(actual);
                        if (!matches) {
                            throw new Win32Exception(13);
                        }
                    } finally {
                        Native.CredFree(saved);
                    }
                } finally {
                    CryptographicOperations.ZeroMemory(secret);
                    Marshal.Copy(secret, 0, blob, secret.Length);
                    Marshal.FreeHGlobal(blob);
                }
            }
        } catch (Exception exception) {
            error = Error(exception);
        } finally {
            if (wrote && !Native.CredDelete(target, 1, 0)) {
                cleanup = Marshal.GetLastWin32Error();
            }
        }
        return new WriteResult(error == 0 && cleanup == 0, error, cleanup);
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Credential
    {
        internal uint Flags, Type;
        internal string TargetName;
        internal string? Comment;
        internal long LastWritten;
        internal uint CredentialBlobSize;
        internal IntPtr CredentialBlob;
        internal uint Persist, AttributeCount;
        internal IntPtr Attributes, TargetAlias;
        internal string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind,
        IntPtr buffer, int size, out int required);
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access,
        out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    internal static extern void CredFree(IntPtr buffer);
}
