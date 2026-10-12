using System.Windows;
using WpfButton = System.Windows.Controls.Button;

namespace NarutoAutoGUI.Views;

// An entry of the compact navigation rail: icon above its label, marked while its page is shown.
public sealed class RailItem : WpfButton
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(RailItem));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }
}
