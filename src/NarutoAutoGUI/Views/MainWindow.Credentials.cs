namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private Task ManageChildSessionCredentialsAsync()
    {
        var (userName, saved) = RefreshChildSessionCredentialStatus();
        new ChildSessionCredentialDialog(_sessionManager.Credentials, userName, saved) { Owner = this }.ShowDialog();
        if (!_allowClose) {
            RefreshChildSessionCredentialStatus();
        }
        return Task.CompletedTask;
    }

    // Settings shows whether a credential is saved and for which account, never the password. Reading it again
    // each time keeps the status right after changes made in Windows Credential Manager.
    private (string? UserName, bool Saved) RefreshChildSessionCredentialStatus()
    {
        try {
            var userName = _sessionManager.Credentials.ReadUserName();
            _settings.ChildSessionCredentials.Status = userName is null ? "未保存" : $"已保存：{userName}";
            return (userName, userName is not null);
        } catch (Exception exception) {
            _logger.Warn("读取保存的 Windows 登录凭据失败。", exception);
            _settings.ChildSessionCredentials.Status = "已保存的凭据无法读取或不完整，可重新保存或删除。";
            return (null, true);
        }
    }
}
