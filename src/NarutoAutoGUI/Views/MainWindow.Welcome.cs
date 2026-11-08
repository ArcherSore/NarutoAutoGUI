using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using NarutoAutoGUI.ProjectModel;

namespace NarutoAutoGUI.Views;

public partial class MainWindow
{
    private static readonly MarkdownStyle WelcomeStyle = new(FontSize: 14, LineHeight: 24, Heading1Size: 20,
        HeadingLineHeight: 28, BrushKey: "Brush.Text.Secondary", StrongBrushKey: "Brush.Text.Primary");
    private IInputElement? _welcomePreviousFocus;
    private string? _welcomeHashToRecord;

    private string WelcomeHashPath => Path.Combine(_applicationDirectory, "config", "welcome-shown.txt");

    private ProjectWelcome? ResolveWelcome() => _projectPlan?.ResolveWelcome(message => _logger.Warn(message));

    private void ShowWelcomeIfChanged()
    {
        if (ResolveWelcome() is { } welcome && welcome.Hash != ReadWelcomeHash()) {
            OpenWelcome(welcome, welcome.Hash);
        }
    }

    // Viewing on request always shows the announcements and leaves what was last acknowledged untouched.
    private void ViewWelcome()
    {
        if (ResolveWelcome() is { } welcome) {
            OpenWelcome(welcome, null);
        }
    }

    private void OpenWelcome(ProjectWelcome welcome, string? hashToRecord)
    {
        if (IsGlobalModalOpen || _exitInProgress || _onboardingActive) {
            return;
        }
        _welcomeHashToRecord = hashToRecord;
        _welcomePreviousFocus = Keyboard.FocusedElement;
        WelcomeTitle.Text = welcome.Title;
        var document = MarkdownDocument.Create(welcome.Contents, OpenTaskDescriptionLink, WelcomeStyle);
        document.PagePadding = new Thickness(0, 0, 12, 0);
        WelcomeViewer.Document = document;
        WelcomeOverlay.Visibility = Visibility.Visible;
        UpdateWelcomeDialogSize();
        EnsureModalWindowHook();
        WelcomeCloseButton.Focus();
        FindVisualChild<ScrollViewer>(WelcomeViewer)?.ScrollToTop();
        if (SystemParameters.ClientAreaAnimation) {
            var duration = TimeSpan.FromMilliseconds(200);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
            WelcomeDialog.BeginAnimation(OpacityProperty, fade);
            var scale = new DoubleAnimation(0.95, 1, duration) { EasingFunction = easing };
            WelcomeDialogScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
            WelcomeDialogScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale);
        }
    }

    private void CloseWelcome_Click(object sender, RoutedEventArgs e) => CloseWelcome();

    private void CloseWelcome()
    {
        if (_exitInProgress) {
            return;
        }
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        RemoveModalWindowHook();
        if (_welcomeHashToRecord is not null) {
            WriteWelcomeHash(_welcomeHashToRecord);
        }
        if (_welcomePreviousFocus is not null) {
            Keyboard.Focus(_welcomePreviousFocus);
        }
        ReevaluateOnboarding();
    }

    private void WelcomeOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWelcomeDialogSize();

    private void UpdateWelcomeDialogSize()
    {
        WelcomeDialog.Width = Math.Min(512, Math.Max(1, WelcomeOverlay.ActualWidth - 32));
        WelcomeDialog.MaxHeight = Math.Max(1, WelcomeOverlay.ActualHeight * 0.8);
    }

    private string? ReadWelcomeHash()
    {
        try {
            return File.Exists(WelcomeHashPath) ? File.ReadAllText(WelcomeHashPath).Trim() : null;
        } catch (Exception exception) {
            _logger.Warn("读取公告已读记录失败。", exception);
            return null;
        }
    }

    private void WriteWelcomeHash(string hash)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(WelcomeHashPath)!);
            File.WriteAllText(WelcomeHashPath, hash);
        } catch (Exception exception) {
            _logger.Warn("保存公告已读记录失败。", exception);
        }
    }
}
