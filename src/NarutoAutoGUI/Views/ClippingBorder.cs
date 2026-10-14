using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfSize = System.Windows.Size;

namespace NarutoAutoGUI.Views;

// A Border that clips its content to the rounded inner corners, so row backgrounds follow the card's outline.
public sealed class ClippingBorder : Border
{
    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (Child is { } child) {
            var radius = Math.Max(0, CornerRadius.TopLeft - BorderThickness.Left);
            child.Clip = new RectangleGeometry(new Rect(child.RenderSize), radius, radius);
        }
        return size;
    }
}
