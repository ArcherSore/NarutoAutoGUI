using System.ComponentModel;
using System.Security.Principal;
using System.Windows.Controls;
using Meziantou.Framework.Win32;
using NarutoAutoGUI.ChildSession;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    // Fake values only; nothing here authenticates against Windows.
    private const string FakeAccount = "MicrosoftAccount\\user@example.invalid";
    private const string FakePassword = "self-test-Pa55word-not-real";

    private static void VerifyChildSessionCredentials(AppLogger logger, string testDirectory)
    {
        VerifyCredentialFallback();
        foreach (var (name, domain, userName) in new[] {
                     (FakeAccount, "", FakeAccount),
                     ("microsoftaccount\\User@Example.invalid", "", "microsoftaccount\\User@Example.invalid"),
                     ("AzureAD\\user@example.invalid", "", "AzureAD\\user@example.invalid"),
                     ("MY-PC\\localuser", "MY-PC", "localuser"), (".\\localuser", ".", "localuser"),
                     ("user@example.invalid", "", "user@example.invalid"), ("localuser", "", "localuser") }) {
            Require(new ChildSessionCredential(name, FakePassword).RdpIdentity == (domain, userName),
                $"RDP 账户拆分错误：{name}。在线身份提供方必须保留完整限定名，本机/域前缀才作为 Domain。");
        }
        Require(!new ChildSessionCredential(FakeAccount, FakePassword).ToString().Contains(FakePassword),
            "凭据的字符串形式不能包含密码。");
        // Read-only: a target that cannot exist takes the real ERROR_NOT_FOUND path without writing anything.
        Require(new WindowsCredentialVault().Read($"NarutoAutoGUI/SelfTest/{Guid.NewGuid():N}") is null,
            "读取不存在的 Windows 凭据必须返回未保存。");
        const string target = ChildSessionCredentialStore.Target;
        var ours = new Credential(CredentialType.Generic, target.ToUpperInvariant(), FakeAccount, FakePassword, null);
        var otherType = new Credential(CredentialType.DomainPassword, target, FakeAccount, FakePassword, null);
        var otherName = new Credential(CredentialType.Generic, target + "/Other", FakeAccount, FakePassword, null);
        Require(WindowsCredentialVault.Find([otherType, otherName, ours], target) == ours
            && WindowsCredentialVault.Find([otherType, otherName], target) is null
            && WindowsCredentialVault.Find([], target) is null,
            "枚举结果只能匹配应用专属目标名（不区分大小写）的 Generic 凭据。");

        var logDirectory = Path.Combine(testDirectory, "credential-logs");
        var vault = new MemoryCredentialVault();
        using (var credentialLogger = new AppLogger(logDirectory)) {
            var store = new ChildSessionCredentialStore(credentialLogger, vault);
            Require(store.Load() is null && store.ReadUserName() is null, "未保存凭据时必须按未配置处理。");

            store.Save($"  {FakeAccount} ", FakePassword);
            Require(store.Load() is { UserName: FakeAccount, Password: FakePassword }
                && store.ReadUserName() == FakeAccount && vault.Targets.Single() == ChildSessionCredentialStore.Target,
                "保存的账户应去除首尾空白，并只写入应用专属的凭据目标。");
            store.Save("MY-PC\\localuser", FakePassword + "2");
            Require(store.Load() is { UserName: "MY-PC\\localuser", Password: FakePassword + "2" },
                "再次保存必须替换原凭据。");
            Require(Throws<ArgumentException>(() => store.Save(" ", FakePassword))
                && Throws<ArgumentException>(() => store.Save(FakeAccount, ""))
                && store.Load()?.UserName == "MY-PC\\localuser", "空账户或空密码不能保存，也不能覆盖已有凭据。");

            vault.Saved = ("MY-PC\\localuser", "");
            Require(store.Load() is null && Throws<InvalidDataException>(() => store.ReadUserName()),
                "不完整的凭据不能提交，Settings 必须把它与未保存区分开。");
            vault.Saved = (null, FakePassword);
            Require(store.Load() is null && Throws<InvalidDataException>(() => store.ReadUserName()),
                "缺少账户的凭据不能提交，Settings 必须把它与未保存区分开。");

            vault.Saved = (FakeAccount, FakePassword);
            vault.ReadFails = true;
            Require(store.Load() is null && Throws<Win32Exception>(() => store.ReadUserName()),
                "无法读取凭据时连接按未保存处理，Settings 则能区分读取失败。");
            vault.Saved = null;
            Require(Throws<Win32Exception>(() => store.ReadUserName()), "读取失败不能被当作未保存。");
            vault.Saved = (FakeAccount, FakePassword);
            vault.ReadFails = false;
            vault.WriteFails = true;
            Require(Throws<Win32Exception>(() => store.Save("MY-PC\\other", FakePassword))
                && store.Load()?.UserName == FakeAccount, "写入失败必须报告，且保留原凭据。");
            vault.WriteFails = false;

            vault.Saved = null;
            VerifyCredentialPromptResults(credentialLogger, testDirectory, store, vault);
        }
        var logText = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(logDirectory, "*.log").Select(File.ReadAllText));
        Require(logText.Contains("已保存桌面分身的 Windows 登录凭据", StringComparison.Ordinal)
            && logText.Contains("已取消保存桌面分身的 Windows 登录凭据", StringComparison.Ordinal)
            && !logText.Contains("Pa55word", StringComparison.Ordinal)
            && !logText.Contains("example.invalid", StringComparison.Ordinal)
            && !logText.Contains("localuser", StringComparison.Ordinal), "日志不得包含账户名或密码。");

        VerifyCredentialSettingsStatus(logger, testDirectory);
    }

    private static void VerifyCredentialPromptResults(AppLogger logger, string testDirectory,
        ChildSessionCredentialStore store, MemoryCredentialVault vault)
    {
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "credential-prompt"), (window, _) => {
            window.SaveChildSessionCredentials(null);
            Require(vault.Saved is null, "取消原生凭据输入不能创建凭据。");
            foreach (var (domain, userName, savedName, rdpDomain, rdpUserName) in new[] {
                         ((string?)null, "localuser", "localuser", "", "localuser"),
                         ("", "  localuser  ", "localuser", "", "localuser"),
                         ("MY-PC", "localuser", "MY-PC\\localuser", "MY-PC", "localuser"),
                         (".", "localuser", ".\\localuser", ".", "localuser"),
                         ("MicrosoftAccount", "user@example.invalid", FakeAccount, "", FakeAccount),
                         ("AzureAD", "user@example.invalid", "AzureAD\\user@example.invalid",
                             "", "AzureAD\\user@example.invalid"),
                         (null, FakeAccount, FakeAccount, "", FakeAccount) }) {
                window.SaveChildSessionCredentials(
                    new CredentialResult(userName, FakePassword, domain, CredentialSaveOption.Hidden));
                var saved = store.Load();
                Require(saved is not null && saved.UserName == savedName && saved.Password == FakePassword
                    && saved.RdpIdentity == (rdpDomain, rdpUserName)
                    && SettingsActionFor(window, "childSession.credentials").Status == $"已保存：{savedName}",
                    "原生输入结果保存后必须保留账户身份，并能按本机/域或在线身份提供方规则提交给 RDP。");
            }
            var previous = vault.Saved;
            window.SaveChildSessionCredentials(null);
            Require(vault.Saved == previous, "取消原生凭据输入不能修改已保存的凭据。");
            Require(Throws<ArgumentException>(() => window.SaveChildSessionCredentials(
                    new CredentialResult("localuser", "", null, CredentialSaveOption.Hidden)))
                && vault.Saved == previous, "原生输入返回空密码时不能覆盖已保存的凭据。");
            vault.WriteFails = true;
            Require(Throws<Win32Exception>(() => window.SaveChildSessionCredentials(
                    new CredentialResult("other", FakePassword, "MY-PC", CredentialSaveOption.Hidden)))
                && vault.Saved == previous, "原生输入结果写入失败必须报告，且保留原凭据。");
            vault.WriteFails = false;
        }, credentials: store);
    }

    private static void VerifyCredentialSettingsStatus(AppLogger logger, string testDirectory)
    {
        var vault = new MemoryCredentialVault();
        var store = new ChildSessionCredentialStore(logger, vault);
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "credential-settings"), (window, _) => {
            var action = SettingsActionFor(window, "childSession.credentials");
            ClickOnboarding(window, "SettingsNavigationItem");
            Require(action.Status == "未保存" && SettingsButtonFor(window, "childSession.credentials").IsEnabled,
                "Settings 默认显示未保存，保存入口可用。");
            store.Save(FakeAccount, FakePassword);
            ClickOnboarding(window, "HomeNavigationItem");
            ClickOnboarding(window, "SettingsNavigationItem");
            var view = (SettingsView)window.FindName("SettingsView");
            Require(action.Status == $"已保存：{FakeAccount}" && !action.Status.Contains(FakePassword)
                && SettingsVisualDescendants(view).OfType<TextBlock>().Any(text => text.Text == action.Status)
                && !SettingsVisualDescendants(view).OfType<TextBlock>().Any(text => text.Text.Contains(FakePassword)),
                "进入 Settings 时必须显示已保存的账户，且从不显示密码。");
            foreach (var (readFails, saved) in new[] {
                         (true, ((string?, string?)?)(FakeAccount, FakePassword)), (false, (FakeAccount, "")) }) {
                vault.ReadFails = readFails;
                vault.Saved = saved;
                ClickOnboarding(window, "HomeNavigationItem");
                ClickOnboarding(window, "SettingsNavigationItem");
                Require(action.Status.StartsWith("已保存的凭据无法读取或不完整", StringComparison.Ordinal)
                    && SettingsButtonFor(window, "childSession.credentials").IsEnabled,
                    "读取失败或凭据不完整时 Settings 必须提示，并保留重新保存入口。");
            }
            vault.ReadFails = false;
            window.SaveChildSessionCredentials(
                new CredentialResult(FakeAccount, FakePassword, null, CredentialSaveOption.Hidden));
            Require(store.ReadUserName() == FakeAccount && action.Status == $"已保存：{FakeAccount}",
                "从不完整的凭据重新保存必须覆盖原条目并刷新状态。");
            vault.Saved = null;
            ClickOnboarding(window, "HomeNavigationItem");
            ClickOnboarding(window, "SettingsNavigationItem");
            Require(action.Status == "未保存", "凭据从应用外删除后，进入 Settings 必须刷新为未保存。");
        }, credentials: store);
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try {
            action();
        } catch (TException) {
            return true;
        }
        return false;
    }

    private static void VerifyCredentialFallback()
    {
        const string target = ChildSessionCredentialStore.Target;
        var direct = new FaultingCredentialVault();
        var scopes = 0;
        var fallback = new CredentialVaultFallback(direct, action => {
            scopes++;
            direct.InScope = true;
            try {
                action();
            } finally {
                direct.InScope = false;
            }
        });
        Require(fallback.Read(target) is null && scopes == 0, "未保存凭据不能触发身份切换。");
        fallback.Write(target, FakeAccount, FakePassword);
        Require(fallback.Read(target)?.Password == FakePassword && scopes == 0,
            "正常读写不应依赖资源管理器或切换身份。");
        direct.DirectError = 1312;
        Require(fallback.Read(target)?.Password == FakePassword && scopes == 1,
            "登录会话缺少凭据集时应使用交互身份读取保存的凭据。");
        fallback.Write(target, FakeAccount, FakePassword + "2");
        Require(direct.Saved?.Password == FakePassword + "2" && scopes == 2 && !direct.InScope,
            "1312 后必须在交互身份下保存用户的新密码，并退出该作用域。");
        var before = scopes;
        direct.DirectError = 5;
        Require(Throws<Win32Exception>(() => fallback.Read(target))
            && Throws<Win32Exception>(() => fallback.Write(target, FakeAccount, FakePassword)) && scopes == before,
            "权限拒绝等非 1312 错误不能切换身份后重试。");
        direct.DirectError = 1312;
        Require(Throws<Win32Exception>(() => fallback.Read(target + "/Other")) && scopes == before,
            "备用访问不能扩大到其他凭据目标。");
        direct.ScopedError = 1312;
        Require(Throws<Win32Exception>(() => fallback.Read(target)) && scopes == before + 1 && !direct.InScope,
            "备用读取仍失败时应报告失败，不能无限重试或伪装为未保存。");
        Require(Throws<Win32Exception>(() => fallback.Write(target, FakeAccount, FakePassword))
            && direct.Saved?.Password == FakePassword + "2" && !direct.InScope,
            "备用保存失败不能报告成功或继续在模拟身份下操作。");
        var unavailable = new CredentialVaultFallback(direct,
            _ => throw new InvalidOperationException("shell unavailable"));
        Require(Throws<InvalidOperationException>(() => unavailable.Read(target))
            && Throws<InvalidOperationException>(() => unavailable.Write(target, FakeAccount, FakePassword)),
            "资源管理器不可用时读写错误必须交回现有调用者处理。");
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var other = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        Require(ShellCredentialAccess.Matches(user, 1, user, 1, false)
            && !ShellCredentialAccess.Matches(user, 1, other, 1, false)
            && !ShellCredentialAccess.Matches(user, 1, user, 2, false)
            && !ShellCredentialAccess.Matches(user, 1, user, 1, true)
            && !ShellCredentialAccess.Matches(null, 1, null, 1, false),
            "备用凭据访问必须拒绝不同用户、不同会话、提权或未知身份的令牌。");
    }

    private sealed class FaultingCredentialVault : ICredentialVault
    {
        internal bool InScope { get; set; }
        internal int DirectError { get; set; }
        internal int ScopedError { get; set; }
        internal (string? UserName, string? Password)? Saved { get; set; }

        public (string? UserName, string? Password)? Read(string target)
        {
            CheckError();
            return Saved;
        }

        public void Write(string target, string userName, string password)
        {
            CheckError();
            Saved = (userName, password);
        }

        private void CheckError()
        {
            var error = InScope ? ScopedError : DirectError;
            if (error != 0) {
                throw new Win32Exception(error);
            }
        }
    }

    // Stands in for WindowsCredentialVault with its semantics: Read returns null only when nothing is saved
    // (CredEnumerate ERROR_NOT_FOUND) and throws Win32Exception for other failures; Write throws like CredWrite.
    private sealed class MemoryCredentialVault : ICredentialVault
    {
        internal (string? UserName, string? Password)? Saved { get; set; }
        internal HashSet<string> Targets { get; } = [];
        internal bool ReadFails { get; set; }
        internal bool WriteFails { get; set; }

        public (string? UserName, string? Password)? Read(string target)
        {
            Targets.Add(target);
            // ERROR_NO_SUCH_LOGON_SESSION: no credential set is associated with the logon session.
            return ReadFails ? throw new Win32Exception(1312, "self-test vault read failure") : Saved;
        }

        public void Write(string target, string userName, string password)
        {
            Targets.Add(target);
            if (WriteFails) {
                throw new Win32Exception(5, "self-test vault write failure");
            }
            Saved = (userName, password);
        }
    }
}
