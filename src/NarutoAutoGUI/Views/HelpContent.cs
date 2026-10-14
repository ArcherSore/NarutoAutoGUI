using NarutoAutoGUI.Settings;

namespace NarutoAutoGUI.Views;

// The FAQ page. Inline markup in answers: 「a → b」 is a UI path, 「x」 a UI label, [[Win]] a key, **x** emphasis.
internal sealed class HelpPageModel : SettingsObservable
{
    private readonly Action<Exception> _copyFailed;
    private bool _qqCopied;
    private int _copies;

    public HelpPageModel(SettingsAction exportDiagnostics, SettingsAction openIssues, Action<Exception> copyFailed)
    {
        ExportDiagnostics = exportDiagnostics;
        OpenIssues = openIssues;
        CopyQq = new SettingsAction(CopyQqAsync);
        _copyFailed = copyFailed;
    }

    public IReadOnlyList<HelpSection> Sections { get; } = [
        new("运行环境与桌面分身", [
            new("首次安装后，准备运行环境失败怎么办？", "先重启电脑，再重新打开 NarutoAutoGUI 准备运行环境。",
                new HelpParagraph("首次安装后，如果桌面分身的运行环境没能准备成功，请先 **重启电脑**，然后再试一次。"),
                HelpSteps.Of(null,
                    "保存正在进行的工作，重启电脑。",
                    "重新打开 NarutoAutoGUI。",
                    "在右侧「运行控制」中再次点击「准备运行环境」。")),
            new("Windows 家庭版输入了正确的密码，却提示密码错误？",
                "要输入 Windows 账户密码而不是 PIN；仍提示错误时，先用密码登录一次 Windows。",
                new HelpParagraph("这里需要输入 **Windows 登录账户的密码**，而不是 PIN。"),
                new HelpChoice(new("微软账户", "输入微软账户的密码。"), new("本地账户", "输入本地账户的密码。")),
                HelpSteps.Of("如果确认密码正确，但仍提示错误：",
                    "打开 Windows「设置 → 账户 → 登录选项」，找到「其他设置」。",
                    "如果有「仅允许对此设备上的 Microsoft 帐户使用 Windows Hello 登录」选项，将其关闭。",
                    "按 [[Win]] + [[L]] 锁定电脑。",
                    "在锁屏界面点击「登录选项」，选择 **密码**，而不是 PIN。",
                    "用账户密码成功登录 Windows 一次。",
                    "重新打开 NarutoAutoGUI，再次尝试创建桌面分身。"),
                new HelpTip("刚修改过微软账户密码？请先用新密码完成一次 Windows 登录，再创建桌面分身。"))
        ]),
        new("任务运行", [
            new("脚本启动后不执行任务怎么办？", "关闭 Windows 任务栏的「自动隐藏」，然后重新运行任务。",
                new HelpParagraph("请先检查 Windows 底部任务栏是否开启了 **自动隐藏**。"),
                HelpSteps.Of(null,
                    "打开 Windows「设置 → 个性化 → 任务栏」，展开「任务栏行为」。",
                    "关闭「自动隐藏任务栏」。",
                    "回到 NarutoAutoGUI，重新运行任务。"))
        ])
    ];

    public SettingsAction ExportDiagnostics { get; }
    public SettingsAction OpenIssues { get; }
    public SettingsAction CopyQq { get; }
    public string Qq { get; } = "368655734";
    public bool QqCopied { get => _qqCopied; private set => Set(ref _qqCopied, value); }

    // The copy button reads 已复制 for two seconds; a later click restarts that period.
    private async Task CopyQqAsync()
    {
        try {
            System.Windows.Clipboard.SetText(Qq);
        } catch (Exception exception) {
            _copyFailed(exception);
            return;
        }
        var copy = ++_copies;
        QqCopied = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (copy == _copies) {
            QqCopied = false;
        }
    }
}

internal sealed record HelpSection(string Title, IReadOnlyList<HelpQuestion> Questions);

internal sealed class HelpQuestion(string title, string summary, params HelpBlock[] blocks) : SettingsObservable
{
    private bool _isExpanded;

    public string Title { get; } = title;
    public string Summary { get; } = summary;
    public IReadOnlyList<HelpBlock> Blocks { get; } = blocks;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
}

// Answer blocks; their spacing lives in the HelpView templates.
internal abstract record HelpBlock;

internal sealed record HelpParagraph(string Text) : HelpBlock;

internal sealed record HelpOption(string Label, string Text);

internal sealed record HelpChoice(params HelpOption[] Options) : HelpBlock;

internal sealed record HelpStep(int Number, string Text);

internal sealed record HelpSteps(string? Title, IReadOnlyList<HelpStep> Items) : HelpBlock
{
    internal static HelpSteps Of(string? title, params string[] steps) =>
        new(title, steps.Select((text, index) => new HelpStep(index + 1, text)).ToArray());
}

internal sealed record HelpTip(string Text) : HelpBlock;
