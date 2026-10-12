using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfPanel = System.Windows.Controls.Panel;
using WpfSize = System.Windows.Size;

namespace NarutoAutoGUI.Views;

// One parameter inspector row: the label cell and the value cell side by side, stacked when the editor is narrow.
// The row draws its own separators, nesting guide and invalid marker so the cells stay plain content.
internal sealed class ParameterRowPanel : WpfPanel
{
    internal const double NarrowWidth = 470;
    internal const double IndentStep = 18;
    private double? _labelWidth;
    private double _labelHeight;
    private bool _invalid;

    internal int Depth { get; init; }
    internal bool Separated { get; init; }
    internal WpfBrush? LineBrush { get; init; }
    internal WpfBrush? GuideBrush { get; init; }
    internal WpfBrush? InvalidBrush { get; init; }

    internal bool IsInvalid
    {
        get => _invalid;
        set {
            _invalid = value;
            InvalidateVisual();
        }
    }

    internal bool IsStacked => _labelWidth is null;

    // The label column takes about a third of the editor, bounded so short and long labels both stay readable.
    internal static double? CalculateLabelWidth(double width) =>
        !double.IsFinite(width) || width < NarrowWidth ? null : Math.Clamp(Math.Round(width * 0.36), 140, 280);

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 600;
        _labelWidth = CalculateLabelWidth(width);
        var (label, value) = (InternalChildren[0], InternalChildren[1]);
        if (_labelWidth is { } labelWidth) {
            label.Measure(new WpfSize(labelWidth, double.PositiveInfinity));
            value.Measure(new WpfSize(Math.Max(0, width - labelWidth - 1), double.PositiveInfinity));
            return new WpfSize(width, Math.Max(44, Math.Max(label.DesiredSize.Height, value.DesiredSize.Height)));
        }
        label.Measure(new WpfSize(width, double.PositiveInfinity));
        value.Measure(new WpfSize(Math.Max(0, width - StackedIndent), double.PositiveInfinity));
        return new WpfSize(width, StackedValueTop(label.DesiredSize.Height) + value.DesiredSize.Height + 4);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var (label, value) = (InternalChildren[0], InternalChildren[1]);
        if (_labelWidth is { } labelWidth) {
            label.Arrange(new Rect(0, 0, labelWidth, finalSize.Height));
            value.Arrange(new Rect(labelWidth + 1, 0, Math.Max(0, finalSize.Width - labelWidth - 1),
                finalSize.Height));
            _labelHeight = finalSize.Height;
        } else {
            _labelHeight = label.DesiredSize.Height;
            label.Arrange(new Rect(0, 0, finalSize.Width, _labelHeight));
            value.Arrange(new Rect(StackedIndent, StackedValueTop(_labelHeight),
                Math.Max(0, finalSize.Width - StackedIndent), value.DesiredSize.Height));
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Separated && LineBrush is not null) {
            dc.DrawRectangle(LineBrush, null, new Rect(0, 0, ActualWidth, 1));
        }
        if (_labelWidth is { } labelWidth && LineBrush is not null) {
            dc.DrawRectangle(LineBrush, null, new Rect(labelWidth, 0, 1, ActualHeight));
        }
        if (Depth > 0 && GuideBrush is not null) {
            dc.DrawRectangle(GuideBrush, null, new Rect(Depth * IndentStep - 2, 0, 2, ActualHeight));
        }
        if (_invalid && InvalidBrush is not null) {
            dc.DrawRectangle(InvalidBrush, null, new Rect(0, 0, 3, _labelHeight));
        }
    }

    // Stacked, the value lines up with the label text and sits just under it.
    private double StackedIndent => Depth * IndentStep + 2;

    private static double StackedValueTop(double labelHeight) => Math.Max(0, labelHeight - 10);
}

// The inputs of one input option side by side, each between 140 and 220 wide, wrapping when the row is narrow.
internal sealed class ParameterInputsPanel : WpfPanel
{
    private const double MinimumWidth = 140;
    private const double MaximumWidth = 220;
    private const double ColumnGap = 12;
    private const double RowGap = 6;
    private double _itemWidth;
    private int _columns = 1;

    internal static int CalculateColumns(double width, int count) => Math.Clamp(
        (int)Math.Floor((width + ColumnGap) / (MinimumWidth + ColumnGap)), 1, Math.Max(1, count));

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MaximumWidth * 2;
        _columns = CalculateColumns(width, InternalChildren.Count);
        _itemWidth = Math.Min(MaximumWidth, Math.Max(0, (width - ColumnGap * (_columns - 1)) / _columns));
        var height = 0d;
        var rowHeight = 0d;
        for (var index = 0; index < InternalChildren.Count; index++) {
            var child = InternalChildren[index];
            child.Measure(new WpfSize(_itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (index % _columns == _columns - 1 || index == InternalChildren.Count - 1) {
                height += rowHeight + (index == InternalChildren.Count - 1 ? 0 : RowGap);
                rowHeight = 0;
            }
        }
        var used = Math.Min(InternalChildren.Count, _columns);
        return new WpfSize(Math.Min(width, used * _itemWidth + (used - 1) * ColumnGap), height);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var top = 0d;
        for (var start = 0; start < InternalChildren.Count; start += _columns) {
            var row = Enumerable.Range(start, Math.Min(_columns, InternalChildren.Count - start))
                .Select(index => InternalChildren[index]).ToArray();
            var height = row.Max(child => child.DesiredSize.Height);
            for (var column = 0; column < row.Length; column++) {
                row[column].Arrange(new Rect(column * (_itemWidth + ColumnGap), top, _itemWidth, height));
            }
            top += height + RowGap;
        }
        return finalSize;
    }
}
