using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProjectModel;

namespace Gui.Views;

// The project's description under the about header, and its contact details as the next row.
public partial class ProjectAboutView : System.Windows.Controls.UserControl
{
    private static readonly MarkdownStyle DescriptionStyle = new(13, 20, 18, 20, "Brush.Text.Secondary");
    private static readonly MarkdownStyle ContactStyle = new(13, 20, 18, 20, "Brush.Text.Muted");

    public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(
        nameof(Project), typeof(ProjectAbout), typeof(ProjectAboutView),
        new PropertyMetadata(null, (view, _) => ((ProjectAboutView)view).Refresh()));
    public static readonly DependencyProperty OpenLinkProperty = DependencyProperty.Register(
        nameof(OpenLink), typeof(Action<Uri>), typeof(ProjectAboutView));

    public ProjectAbout? Project
    {
        get => (ProjectAbout?)GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public Action<Uri>? OpenLink
    {
        get => (Action<Uri>?)GetValue(OpenLinkProperty);
        set => SetValue(OpenLinkProperty, value);
    }

    public ProjectAboutView()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        Show(Description, Description, Project?.Description, "查看项目简介", DescriptionStyle,
            TextAlignment.Center);
        Show(ContactRow, Contact, Project?.Contact, "查看联系方式", ContactStyle, TextAlignment.Left);
    }

    private void Show(UIElement container, ContentControl host, ProjectText? text, string linkLabel,
        MarkdownStyle style, TextAlignment alignment)
    {
        host.Content = Render(text, linkLabel, style, alignment);
        container.Visibility = host.Content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement? Render(ProjectText? text, string linkLabel, MarkdownStyle style, TextAlignment alignment)
    {
        if (text?.Error is { } error) {
            var message = new TextBlock { Text = error, TextAlignment = alignment };
            message.SetResourceReference(StyleProperty, "SecondaryTextStyle");
            return message;
        }
        var markdown = text?.Url is { } url ? $"[{linkLabel} ↗](<{url.AbsoluteUri}>)" : text?.Markdown;
        if (markdown is null) {
            return null;
        }
        var document = MarkdownDocument.Create([markdown], uri => OpenLink?.Invoke(uri), style);
        document.TextAlignment = alignment;
        document.PagePadding = new Thickness(0);
        var viewer = new FlowDocumentScrollViewer {
            Document = document, IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        // The settings page owns scrolling; the viewer's own scroller would swallow the wheel.
        viewer.PreviewMouseWheel += (_, e) => {
            e.Handled = true;
            RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
        };
        return viewer;
    }
}
