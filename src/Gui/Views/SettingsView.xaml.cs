using System.Windows;
using System.Windows.Controls;

namespace Gui.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    // A section landed on from the list keeps this gap above it, and counts as current once it passes the line.
    private const double SectionTopGap = 16;
    private const double CurrentSectionLine = 60;
    private const double ContentBottomMargin = 24;

    private bool _followingScroll;

    public SettingsView()
    {
        InitializeComponent();
    }

    private void SectionNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_followingScroll || SectionAt(SectionNavigation.SelectedIndex) is not { } section) { return; }
        SectionScroller.ScrollToVerticalOffset(SectionScroller.VerticalOffset + TopOf(section) - SectionTopGap);
    }

    private void SectionScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SectionScroller)) { return; }
        var count = SectionList.Items.Count;
        if (SectionAt(count - 1) is not { } last) { return; }
        // The last section stays tall enough to reach the top, so every list entry has a place to land.
        last.MinHeight = Math.Max(0, e.ViewportHeight - SectionTopGap - ContentBottomMargin);
        _followingScroll = true;
        SectionNavigation.SelectedIndex = Enumerable.Range(0, count)
            .LastOrDefault(index => SectionAt(index) is { } section && TopOf(section) <= CurrentSectionLine);
        _followingScroll = false;
    }

    private FrameworkElement? SectionAt(int index)
        => SectionList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private double TopOf(FrameworkElement section) => section.TranslatePoint(default, SectionScroller).Y;
}
