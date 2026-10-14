using System.Windows;
using System.Windows.Controls;
using WpfSize = System.Windows.Size;

namespace NarutoAutoGUI.Views;

// Shows the top Reveal fraction (0 to 1) of its child, so animating Reveal slides the child open or shut.
public sealed class RevealDecorator : Decorator
{
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal),
        typeof(double), typeof(RevealDecorator),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnRevealChanged));

    public RevealDecorator() => ClipToBounds = true;

    public double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        UpdateChildVisibility();
    }

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        var size = base.MeasureOverride(new WpfSize(constraint.Width, double.PositiveInfinity));
        return new WpfSize(size.Width, size.Height * Reveal);
    }

    protected override WpfSize ArrangeOverride(WpfSize arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }

    private static void OnRevealChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) =>
        ((RevealDecorator)element).UpdateChildVisibility();

    // A fully hidden child keeps its size for the next animation but leaves the accessibility tree.
    private void UpdateChildVisibility()
    {
        if (Child is not null) {
            Child.Visibility = Reveal > 0 ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
