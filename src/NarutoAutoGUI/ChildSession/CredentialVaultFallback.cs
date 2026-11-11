using System.ComponentModel;

namespace NarutoAutoGUI.ChildSession;

// Only the fixed Child Session credential may cross into the interactive user's logon context.
internal sealed class CredentialVaultFallback(ICredentialVault direct, Action<Action>? impersonate = null)
    : ICredentialVault
{
    private readonly Action<Action> _impersonate = impersonate ?? ShellCredentialAccess.Run;

    public (string? UserName, string? Password)? Read(string target) => Execute(target, () => direct.Read(target));

    public void Write(string target, string userName, string password) => Execute(target, () => {
        direct.Write(target, userName, password);
        return true;
    });

    private T Execute<T>(string target, Func<T> operation)
    {
        try {
            return operation();
        } catch (Win32Exception exception) when (exception.NativeErrorCode == 1312
            && string.Equals(target, ChildSessionCredentialStore.Target, StringComparison.OrdinalIgnoreCase)) {
            // No logging, RDP or scheduling inside this scope. RunImpersonated restores identity on exceptions too.
            T result = default!;
            _impersonate(() => result = operation());
            return result;
        }
    }
}
