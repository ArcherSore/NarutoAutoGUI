using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using NarutoAutoGUI.ProjectModel;
using NarutoAutoGUI.Views;

namespace NarutoAutoGUI.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void SetAbout(string directory, string? description, string? contact, string? license,
        string? github = null) => EditInterface(directory, root => {
            root["description"] = description;
            root["contact"] = contact;
            root["license"] = license;
            root["github"] = github;
            root["task"]![0]!["description"] = "TASK_DESCRIPTION_MUST_NOT_APPEAR_IN_ABOUT";
        });

    private static void VerifyProjectAboutContent(string testDirectory)
    {
        var directory = CreateProjectFixture(Path.Combine(testDirectory, "about-content"));
        ProjectAbout Load() => ProjectPlanModule.Open(directory, Path.Combine(directory, "about-config.json")).About;
        Require(Load() is { Description: null, Contact: null, License: null }, "缺失的项目资料必须为空。");
        SetAbout(directory, " ", "", null, " ");
        Require(Load() is { Description: null, Contact: null, License: null, Github: null },
            "空白项目资料必须隐藏，不能取 task.description。");
        var inline = "# MaaNOP\n\n**项目简介** [链接](https://example.com/info)";
        SetAbout(directory, inline, "$contact", "MIT");
        Require(Load() is { Description.Markdown: var markdown, Contact.Markdown: "contact", License.Markdown: "MIT" }
            && markdown == inline, "Markdown 原样保留，翻译键显示为键名，读不到的 MIT 之类按文本显示。");
        SetAbout(directory, "支持 Windows/Linux 自动化", "联系 https://example.com/contact", null);
        Require(Load() is { Description.Markdown: "支持 Windows/Linux 自动化",
            Contact.Markdown: "联系 https://example.com/contact" }, "内联正文中的斜线或链接不能被误认为文件引用。");

        Directory.CreateDirectory(Path.Combine(directory, "docs"));
        File.WriteAllText(Path.Combine(directory, "docs", "about.md"), "文件简介");
        File.WriteAllText(Path.Combine(directory, "contact info[zh].txt"), "文件联系方式");
        File.WriteAllText(Path.Combine(directory, "LICENSE"), "许可证正文");
        SetAbout(directory, "docs/about.md", "contact info[zh].txt", "LICENSE");
        var snapshot = Load();
        Require(snapshot is { Description.Markdown: "文件简介", Contact.Markdown: "文件联系方式",
            License.Markdown: "许可证正文" }, "相对 PI 的文件和无扩展名 LICENSE 必须被读取。");
        File.WriteAllText(Path.Combine(directory, "LICENSE"), "新的许可证");
        SetAbout(directory, "新简介", null, "LICENSE");
        Require(snapshot is { Description.Markdown: "文件简介", License.Markdown: "许可证正文" }
            && Load().License?.Markdown == "新的许可证", "资料必须来自同一次加载快照，重载时才刷新。");

        var outside = Path.GetDirectoryName(directory)!;
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "不能读取的正文");
        foreach (var path in new[] { "../outside.txt", "missing.md", Path.Combine(outside, "outside.txt"),
            "docs/../../outside.txt", "./LICENSE:stream" }) {
            SetAbout(directory, path, path, path);
            var project = ProjectPlanModule.Open(directory, Path.Combine(directory, "about-config.json"));
            Require(project.Tasks.Count > 0 && project.About is { Description.Error: not null,
                Contact.Error: not null, License.Error: not null, Description.Markdown: null },
                $"无法读取或越界的明确文件引用必须局部报错，不阻止任务加载：{path}");
        }
        SetAbout(directory, "docs/about.md", null, null);
        using (new FileStream(Path.Combine(directory, "docs", "about.md"), FileMode.Open, FileAccess.Read,
            FileShare.None)) {
            Require(Load().Description?.Error is not null, "无法读取的已存在文件必须局部报错。");
        }
        foreach (var url in new[] { "http://127.0.0.1:1/no-fetch", "https://example.invalid/LICENSE" }) {
            SetAbout(directory, url, url, url);
            var about = Load();
            Require(about.Description?.Url?.AbsoluteUri == url && about.Contact?.Url?.AbsoluteUri == url
                && about.License?.Url?.AbsoluteUri == url, "HTTP(S) 必须仅保留链接，不抓取远端正文。");
        }
        foreach (var github in new[] { "https://github.com/owner/repo", "https://github.com:443/owner/repo/",
            "https://GITHUB.COM/owner_name/repo.git" }) {
            SetAbout(directory, null, null, null, github);
            Require(Load().Github is not null, $"应接受 Update Engine 支持的仓库：{github}");
        }
        foreach (var github in new[] { "http://github.com/owner/repo", "https://example.com/owner/repo",
            "https://github.com:444/owner/repo", "https://user@github.com/owner/repo",
            "https://github.com/owner/repo?x=y", "https://github.com/owner/repo#x",
            "https://github.com/owner/repo/more", "https://github.com/owner/r%2fepo",
            "https://github.com/own%65r/repo", "https://github.com/owner/repo%2egit",
            "https://github.com/owner/repo?", "https://github.com/owner/repo#" }) {
            SetAbout(directory, null, null, null, github);
            Require(Load().Github is null, $"应隐藏 Update Engine 拒绝的仓库：{github}");
        }
    }

    private static void VerifyProjectAboutView(AppLogger logger, string testDirectory)
    {
        var longLicense = string.Join("\n\n", Enumerable.Repeat("许可证正文，可滚动阅读。", 100));
        RunOnboardingScenario(logger, Path.Combine(testDirectory, "about-view"), (window, directory) => {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            var settings = OpenAboutSection(window);
            PumpUntil(() => SettingsVisualDescendants(settings).OfType<ProjectAboutView>().Any(),
                "关于页必须显示项目资料。");
            var view = SettingsVisualDescendants(settings).OfType<ProjectAboutView>().Single();
            PumpUntil(() => view.TranslatePoint(default, settings).Y is >= 0 and var top && top < settings.ActualHeight,
                "目录必须将关于内容滚动到可见区域。");
            var opened = new List<Uri>();
            SettingsPageFor(window).Sections.SelectMany(section => section.Items)
                .Single(item => item.About is not null).About!.OpenLink = opened.Add;
            var repository = SettingsActionFor(window, "project.openRepository");
            var license = SettingsActionFor(window, "project.viewLicense");
            Require(AboutText(view).Contains("项目简介") && AboutText(view).Contains("联系作者")
                && !AboutText(view).Contains("TASK_DESCRIPTION") && !AboutText(view).Contains("许可证正文"),
                "关于页只渲染顶层简介和联系方式，许可证全文只能在阅读窗口显示。");
            Require(SettingsButtonFor(window, "project.openRepository").IsVisible
                && SettingsButtonFor(window, "project.viewLicense").IsVisible, "仓库和许可证入口必须显示。");
            Require(opened.Count == 0, "加载资料不得打开浏览器。");
            CaptureAbout(window, "about");
            repository.ExecuteAsync().GetAwaiter().GetResult();
            Require(opened.Single().AbsoluteUri == "https://github.com/owner/repo", "仓库按钮必须打开 PI 仓库。");
            ClickAboutLinks(view);
            Require(opened.Last().AbsoluteUri == "https://example.com/info", "Markdown 链接必须复用点击处理。");

            var scroller = Named<ScrollViewer>(settings, "SectionScroller");
            var offset = scroller.VerticalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) {
                RoutedEvent = UIElement.PreviewMouseWheelEvent
            };
            ((UIElement)Named<ContentControl>(view, "Description").Content).RaiseEvent(wheel);
            PumpUntil(() => scroller.VerticalOffset < offset, "鼠标在简介上滚动时必须滚动设置页。");

            license.ExecuteAsync().GetAwaiter().GetResult();
            PumpUntil(() => Named<UIElement>(window, "WelcomeOverlay").IsVisible, "点击许可证必须打开阅读窗口。");
            var reader = Named<FlowDocumentScrollViewer>(window, "WelcomeViewer");
            Require(Named<TextBlock>(window, "WelcomeTitle").Text == "许可证"
                && TextOf(reader.Document).Contains("许可证正文"), "阅读窗口必须显示完整许可证。");
            PumpUntil(() => SettingsVisualDescendants(reader).OfType<ScrollViewer>().Any(s => s.ScrollableHeight > 0),
                "长许可证必须可以滚动。");
            CaptureAbout(window, "license");
            ClickOnboarding(window, "WelcomeDismissButton");
            PumpUntil(() => !Named<UIElement>(window, "WelcomeOverlay").IsVisible, "阅读窗口必须可以关闭。");

            SetAbout(directory, "http://example.com/description", "https://example.com/contact",
                "https://example.com/license", "invalid");
            InvokeOnboarding(window, "LoadProject");
            PumpUntil(() => !repository.IsVisible, "重载后无效仓库必须隐藏。");
            ClickAboutLinks(view);
            license.ExecuteAsync().GetAwaiter().GetResult();
            Require(opened.Skip(2).Select(uri => uri.AbsoluteUri).SequenceEqual([
                "http://example.com/description", "https://example.com/contact", "https://example.com/license"]),
                "三个内容字段的 URL 都必须由用户点击后直接打开。");
            SetAbout(directory, "missing.md", "联系作者", "missing.txt");
            InvokeOnboarding(window, "LoadProject");
            PumpUntil(() => license is { IsEnabled: false, Status.Length: > 0 }, "许可证错误必须显示在对应行。");
            Require(Named<ContentControl>(view, "Description").Content is TextBlock { Text.Length: > 0 }
                && AboutText(view).Contains("联系作者"), "一个文件错误不能隐藏其他项目资料。");
            SetAbout(directory, null, " ", "", null);
            InvokeOnboarding(window, "LoadProject");
            PumpUntil(() => Named<UIElement>(view, "Description").Visibility == Visibility.Collapsed
                && Named<UIElement>(view, "ContactRow").Visibility == Visibility.Collapsed
                && !SettingsButtonFor(window, "project.viewLicense").IsVisible, "重载为空后必须隐藏所有对应内容及入口。");
        }, beforeLoad: directory => SetAbout(directory, "项目简介 [说明](https://example.com/info)",
            "联系作者", longLicense, "https://github.com/owner/repo"));
    }

    private static SettingsView OpenAboutSection(MainWindow window)
    {
        ClickOnboarding(window, "SettingsNavigationItem");
        var settings = Named<SettingsView>(window, "SettingsView");
        var navigation = Named<System.Windows.Controls.ListBox>(settings, "SectionNavigation");
        navigation.SelectedIndex = navigation.Items.Count - 1;
        return settings;
    }

    private static string TextOf(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text;

    // The rendered content, not the visual tree: a reload replaces it before the next layout pass.
    private static IEnumerable<FlowDocument> AboutDocuments(ProjectAboutView view) =>
        new[] { "Description", "Contact" }.Select(name => Named<ContentControl>(view, name).Content)
            .OfType<FlowDocumentScrollViewer>().Select(viewer => viewer.Document);

    private static string AboutText(ProjectAboutView view) => string.Join("\n", AboutDocuments(view).Select(TextOf));

    private static void ClickAboutLinks(ProjectAboutView view)
    {
        foreach (var link in AboutDocuments(view).SelectMany(document => document.Blocks.OfType<Paragraph>())
            .SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>())) {
            link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri, "") {
                RoutedEvent = Hyperlink.RequestNavigateEvent
            });
        }
    }

    private static void CaptureAbout(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("NARUTO_SUPPORT_QA_DIR") is not { Length: > 0 } output) { return; }
        Directory.CreateDirectory(output);
        PumpOnboarding(300);
        SaveScreenshot(window, Path.Combine(output, $"project-{name}.png"));
    }
}
