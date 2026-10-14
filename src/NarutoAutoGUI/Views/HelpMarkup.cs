using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WpfBinding = System.Windows.Data.Binding;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace NarutoAutoGUI.Views;

// Fills a TextBlock from FAQ markup: 「a → b」 and 「x」 become UI-label chips, [[Win]] a key cap, **x** emphasis.
public static partial class HelpMarkup
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(HelpMarkup), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    [GeneratedRegex(@"「([^」]+)」|\[\[([^\]]+)\]\]|\*\*([^*]+)\*\*")]
    private static partial Regex Token();

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var host = (TextBlock)element;
        var inlines = host.Inlines;
        inlines.Clear();
        var text = (string?)e.NewValue ?? "";
        var position = 0;
        foreach (Match match in Token().Matches(text)) {
            if (match.Index > position) {
                inlines.Add(new Run(text[position..match.Index]));
            }
            inlines.Add(match.Groups[1].Success ? Chip(match.Groups[1].Value, host)
                : match.Groups[2].Success ? Key(match.Groups[2].Value) : Strong(match.Groups[3].Value));
            position = match.Index + match.Length;
        }
        if (position < text.Length) {
            inlines.Add(new Run(text[position..]));
        }
    }

    // Chip text follows the host's size, so a chip in a 12 px summary stays 12 px.
    private static Inline Chip(string label, TextBlock host)
    {
        var parts = new StackPanel { Orientation = WpfOrientation.Horizontal };
        foreach (var part in label.Split('→', StringSplitOptions.TrimEntries)) {
            if (parts.Children.Count > 0) {
                parts.Children.Add(ChipText("›", "Help.ChipSeparator", host));
            }
            parts.Children.Add(ChipText(part, "Help.ChipText", host));
        }
        return Container(Styled(new Border { Child = parts }, "Help.Chip"));
    }

    private static TextBlock ChipText(string text, string style, TextBlock host)
    {
        var block = Styled(new TextBlock { Text = text }, style);
        block.SetBinding(TextBlock.FontSizeProperty, new WpfBinding(nameof(TextBlock.FontSize)) { Source = host });
        return block;
    }

    private static Inline Key(string key) =>
        Container(Styled(new Border { Child = Styled(new TextBlock { Text = key }, "Help.KeyText") }, "Help.Key"));

    private static Inline Strong(string text)
    {
        var run = new Run(text) { FontWeight = FontWeights.SemiBold };
        run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Primary");
        return run;
    }

    private static InlineUIContainer Container(UIElement child) =>
        new(child) { BaselineAlignment = BaselineAlignment.Center };

    private static T Styled<T>(T element, string style) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, style);
        return element;
    }
}
