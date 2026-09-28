using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NarutoAutoGUI.Views;

// Shows an edge fade only while content is clipped on that edge: values are VerticalOffset and ScrollableHeight.
public sealed class ScrollEdgeVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double offset, double scrollable]) {
            return Visibility.Collapsed;
        }
        var clipped = Equals(parameter, "Top") ? offset > 0.5 : offset < scrollable - 0.5;
        return clipped ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
