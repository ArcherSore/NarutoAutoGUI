using System.Windows;
using System.Windows.Markup;
using Wpf.Ui.Controls;

namespace NarutoAutoGUI.Views;

// Keep the bell, badge and loading indicator together in NavigationView's persistent icon slot.
[ContentProperty(nameof(Child))]
public sealed class NavigationIcon : IconElement
{
    public UIElement Child { get; set; } = null!;

    protected override UIElement InitializeChildren() => Child;
}
