using Meziantou.Framework.Win32;
using NarutoAutoGUI.Infrastructure;

namespace NarutoAutoGUI.ChildSession;

// The Windows account a Child Session logs on with when Windows cannot reuse the current sign-in, e.g. a PIN.
// A class rather than a record, so no generated ToString can print the password.
internal sealed class ChildSessionCredential(string userName, string password)
{
    internal string UserName { get; } = userName;
    internal string Password { get; } = password;

    // RDP takes the account as Domain + UserName. Online identity providers keep their qualified name as the
    // UserName (see sinpoce/ParaDesk a0ae7f6); only a machine or AD domain prefix becomes the Domain.
    internal (string Domain, string UserName) RdpIdentity
    {
        get
        {
            var separator = UserName.IndexOf('\\');
            if (separator <= 0) {
                return ("", UserName);
            }
            var prefix = UserName[..separator];
            return prefix.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("AzureAD", StringComparison.OrdinalIgnoreCase)
                ? ("", UserName) : (prefix, UserName[(separator + 1)..]);
        }
    }

    public override string ToString() => nameof(ChildSessionCredential);
}

// Keeps the optional Child Session logon in Windows Credential Manager, encrypted for the current Windows user.
// Saving only stores the account; Windows decides whether it is valid when the RDP logon runs.
// Neither the account name nor the password is ever logged.
internal sealed class ChildSessionCredentialStore
{
    internal const string Target = "NarutoAutoGUI/ChildSession/WindowsLogon";

    private readonly AppLogger _logger;
    private readonly ICredentialVault _vault;

    // The vault is a self-test seam; production always uses Windows Credential Manager.
    internal ChildSessionCredentialStore(AppLogger logger, ICredentialVault? vault = null)
    {
        _logger = logger;
        _vault = vault ?? new WindowsCredentialVault();
    }

    // Shows what is saved: null when nothing is, and throws when the store cannot be read or holds an incomplete
    // credential, so Settings can still offer to replace it. Windows returns the decrypted secret with
    // every read of a generic credential, so the password is read here too; only the account name is kept.
    internal string? ReadUserName() => Read()?.UserName;

    // Used only for the RDP logon. A credential that is missing, unreadable or incomplete is not submitted, so the
    // logon falls back to the Windows prompt as before; only a failed read is logged as a warning.
    internal ChildSessionCredential? Load()
    {
        try {
            return Read();
        } catch (Exception exception) {
            _logger.Warn("读取保存的 Windows 登录凭据失败，本次连接按未保存处理。", exception);
            return null;
        }
    }

    internal void Save(string userName, string password)
    {
        userName = userName.Trim();
        if (userName.Length == 0 || password.Length == 0) {
            throw new ArgumentException("账户和密码都不能为空。");
        }
        try {
            _vault.Write(Target, userName, password);
        } catch (Exception exception) {
            _logger.Warn("保存 Windows 登录凭据失败。", exception);
            throw;
        }
        _logger.Info("已保存桌面分身的 Windows 登录凭据。");
    }

    private ChildSessionCredential? Read()
    {
        if (_vault.Read(Target) is not var (userName, password)) {
            return null;
        }
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) {
            throw new InvalidDataException("保存的 Windows 登录凭据不完整。");
        }
        return new ChildSessionCredential(userName, password);
    }
}

internal interface ICredentialVault
{
    // Null only when nothing is saved under the target; any failure to read the store throws.
    (string? UserName, string? Password)? Read(string target);

    void Write(string target, string userName, string password);
}

internal sealed class WindowsCredentialVault : ICredentialVault
{
    // ReadCredential (CredRead) returns null for every failure. EnumerateCredentials (CredEnumerate) returns an
    // empty list only for ERROR_NOT_FOUND and throws Win32Exception otherwise, e.g. ERROR_NO_SUCH_LOGON_SESSION.
    public (string? UserName, string? Password)? Read(string target)
    {
        var saved = Find(CredentialManager.EnumerateCredentials(target), target);
        return saved is null ? null : (saved.UserName, saved.Password);
    }

    // The filter may match other credential types or, with wildcards, other names. Name and type identify a
    // credential, and Windows compares names without case.
    internal static Credential? Find(IEnumerable<Credential> credentials, string target) =>
        credentials.FirstOrDefault(credential => credential.CredentialType == CredentialType.Generic
            && string.Equals(credential.ApplicationName, target, StringComparison.OrdinalIgnoreCase));

    // LocalMachine keeps the credential for this Windows user on this PC across sign-ins, without roaming.
    public void Write(string target, string userName, string password) => CredentialManager.WriteCredential(
        target, userName, password, "NarutoAutoGUI 桌面分身自动登录", CredentialPersistence.LocalMachine);
}
