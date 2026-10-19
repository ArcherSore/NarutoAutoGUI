using System.Windows;
using NarutoAutoGUI.ChildSession;

namespace NarutoAutoGUI.Views;

// Saves, replaces or deletes the Windows logon a Child Session uses. A saved password is never read back.
internal partial class ChildSessionCredentialDialog
{
    private readonly ChildSessionCredentialStore _store;

    internal ChildSessionCredentialDialog(ChildSessionCredentialStore store, string? savedUserName, bool saved)
    {
        InitializeComponent();
        _store = store;
        UserNameBox.Text = savedUserName ?? string.Empty;
        DeleteButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => (savedUserName is null ? (UIElement)UserNameBox : PasswordInput).Focus();
        Closed += (_, _) => PasswordInput.Clear();
    }

    // False keeps the dialog open with the reason shown.
    internal bool TrySave()
    {
        var userName = UserNameBox.Text.Trim();
        if (userName.Length == 0) {
            return Fail("请填写 Windows 账户。", UserNameBox);
        }
        if (PasswordInput.SecurePassword.Length == 0) {
            return Fail("请填写 Windows 账户密码（不是 PIN）。", PasswordInput);
        }
        try {
            _store.Save(userName, PasswordInput.Password);
        } catch (Exception exception) {
            return Fail($"无法保存凭据：{exception.Message}", SaveButton);
        }
        PasswordInput.Clear();
        return true;
    }

    internal bool TryDelete()
    {
        try {
            _store.Delete();
        } catch (Exception exception) {
            return Fail($"无法删除凭据：{exception.Message}", DeleteButton);
        }
        return true;
    }

    private bool Fail(string message, UIElement focus)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        focus.Focus();
        return false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TrySave()) {
            DialogResult = true;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TryDelete()) {
            DialogResult = true;
        }
    }
}
