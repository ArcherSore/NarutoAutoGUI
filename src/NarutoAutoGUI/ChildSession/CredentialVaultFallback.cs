using System.ComponentModel;

namespace NarutoAutoGUI.ChildSession;

// Only the fixed Child Session credential may cross into the interactive user's logon context.
internal sealed class CredentialVaultFallback(ICredentialVault direct, Action<Action> impersonate) : ICredentialVault
{
    private const int ErrorNoSuchLogonSession = 1312;

    public (string? UserName, string? Password)? Read(string target)
    {
        (string? UserName, string? Password)? saved = null;
        Execute(target, () => saved = direct.Read(target));
        return saved;
    }

    public void Write(string target, string userName, string password) =>
        Execute(target, () => direct.Write(target, userName, password));

    private void Execute(string target, Action operation)
    {
        try {
            operation();
        } catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorNoSuchLogonSession
            && string.Equals(target, ChildSessionCredentialStore.Target, StringComparison.OrdinalIgnoreCase)) {
            // No logging, RDP or scheduling inside this scope. RunImpersonated restores identity on exceptions too.
            impersonate(operation);
        }
    }
}
