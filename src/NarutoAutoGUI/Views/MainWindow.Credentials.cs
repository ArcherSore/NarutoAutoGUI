using System.Windows.Interop;
using Meziantou.Framework.Win32;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private Task ManageChildSessionCredentialsAsync()
    {
        var userName = RefreshChildSessionCredentialStatus();
        try {
            var credential = CredentialManager.PromptForCredentials(new WindowInteropHelper(this).Handle,
                "请输入当前 Windows 登录账户及其密码（不是 PIN）。\r\n"
                + "本地账户：填写 计算机名\\用户名 或 .\\用户名，使用本地账户密码。\r\n"
                + "Microsoft 账户：填写 MicrosoftAccount\\邮箱，使用 Microsoft 账户密码。\r\n"
                + "保存时不验证密码，请创建桌面分身确认。",
                "桌面分身自动登录", userName, CredentialSaveOption.Hidden);
            SaveChildSessionCredentials(credential);
        } catch (Exception exception) {
            _logger.Warn("打开或保存 Windows 登录凭据失败。", exception);
            ShowActionableError("无法保存 Windows 登录凭据", exception,
                "请重新填写 Windows 账户和账户密码（不是 PIN）。", offerLogDirectory: false);
        }
        return Task.CompletedTask;
    }

    // The native prompt separates Domain from UserName; retain the qualified account before the RDP split.
    internal void SaveChildSessionCredentials(CredentialResult? credential)
    {
        if (credential is null) {
            return;
        }
        var userName = string.IsNullOrEmpty(credential.Domain)
            ? credential.UserName : $"{credential.Domain}\\{credential.UserName}";
        _sessionManager.Credentials.Save(userName, credential.Password);
        if (!_allowClose) {
            RefreshChildSessionCredentialStatus();
        }
    }

    // Settings shows whether a credential is saved and for which account, never the password. Reading it again
    // each time keeps the status right after changes made in Windows Credential Manager.
    private string? RefreshChildSessionCredentialStatus()
    {
        try {
            var userName = _sessionManager.Credentials.ReadUserName();
            _settings.ChildSessionCredentials.Status = userName is null ? "未保存" : $"已保存：{userName}";
            return userName;
        } catch (Exception exception) {
            _logger.Warn("读取保存的 Windows 登录凭据失败。", exception);
            _settings.ChildSessionCredentials.Status = "已保存的凭据无法读取或不完整，可重新保存。";
            return null;
        }
    }
}
