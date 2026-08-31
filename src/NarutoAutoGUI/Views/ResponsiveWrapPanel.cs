using System.Windows;
using System.Windows.Controls;
using WpfPanel = System.Windows.Controls.Panel;
using WpfSize = System.Windows.Size;

namespace NarutoAutoGUI.Views;

internal sealed class ResponsiveWrapPanel : WpfPanel
{
    private readonly List<LayoutSlot> _slots = [];
    private double _measuredWidth;

    internal double MinimumItemWidth { get; init; } = 220;
    internal double HorizontalSpacing { get; init; } = 12;
    internal double VerticalSpacing { get; init; } = 12;
    internal int MaximumColumns { get; init; } = 3;

    internal static int CalculateColumnCount(
        double availableWidth, double minimumItemWidth, double spacing, int maximum)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) {
            return 1;
        }
        return Math.Clamp((int)Math.Floor((availableWidth + spacing) / (minimumItemWidth + spacing)), 1, maximum);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : MinimumItemWidth;
        _measuredWidth = width;
        return new WpfSize(width, BuildLayout(width));
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        if (Math.Abs(finalSize.Width - _measuredWidth) > 0.5) {
            _measuredWidth = finalSize.Width;
            _ = BuildLayout(finalSize.Width);
        }
        foreach (var slot in _slots) {
            slot.Child.Arrange(slot.Bounds);
        }
        return finalSize;
    }

    private double BuildLayout(double availableWidth)
    {
        _slots.Clear();
        if (InternalChildren.Count == 0 || availableWidth <= 0) {
            return 0;
        }

        var columns = CalculateColumnCount(availableWidth, MinimumItemWidth, HorizontalSpacing, MaximumColumns);
        var itemWidth = Math.Max(0, (availableWidth - HorizontalSpacing * (columns - 1)) / columns);
        var pending = new List<UIElement>(columns);
        var verticalOffset = 0d;

        foreach (UIElement child in InternalChildren) {
            child.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
            var needsFullWidth = columns > 1 && child.DesiredSize.Width > itemWidth + 24;
            if (needsFullWidth) {
                verticalOffset = AddRow(pending, itemWidth, verticalOffset);
                child.Measure(new WpfSize(availableWidth, double.PositiveInfinity));
                _slots.Add(new LayoutSlot(
                    child, new Rect(0, verticalOffset, availableWidth, child.DesiredSize.Height)));
                verticalOffset += child.DesiredSize.Height + VerticalSpacing;
                continue;
            }

            pending.Add(child);
            if (pending.Count == columns) {
                verticalOffset = AddRow(pending, itemWidth, verticalOffset);
            }
        }

        verticalOffset = AddRow(pending, itemWidth, verticalOffset);
        return Math.Max(0, verticalOffset - VerticalSpacing);
    }

    private double AddRow(List<UIElement> children, double itemWidth, double verticalOffset)
    {
        if (children.Count == 0) {
            return verticalOffset;
        }

        var rowHeight = 0d;
        for (var index = 0; index < children.Count; index++) {
            var child = children[index];
            child.Measure(new WpfSize(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            var left = index * (itemWidth + HorizontalSpacing);
            _slots.Add(new LayoutSlot(child, new Rect(left, verticalOffset, itemWidth, child.DesiredSize.Height)));
        }
        children.Clear();
        return verticalOffset + rowHeight + VerticalSpacing;
    }

    private sealed record LayoutSlot(UIElement Child, Rect Bounds);
}
