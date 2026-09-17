using System.Windows;
using System.Windows.Documents;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.TaskLists;
using MarkdownTable = Markdig.Extensions.Tables.Table;
using MarkdownTableRow = Markdig.Extensions.Tables.TableRow;
using MarkdownTableCell = Markdig.Extensions.Tables.TableCell;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace NarutoAutoGUI.Views;

// Render release text as native document elements: no HTML, scripts or remote image loading.
internal static class ReleaseNotesDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks().UsePipeTables().UseTaskLists().UseEmphasisExtras().DisableHtml().Build();

    public static FlowDocument Create(string markdown, Action<Uri> openLink)
    {
        var document = new FlowDocument {
            PagePadding = new Thickness(0, 0, 12, 8), FontSize = 13, ColumnWidth = 10000,
            TextAlignment = TextAlignment.Left
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.Text.Body");
        document.SetResourceReference(FlowDocument.FontFamilyProperty, "FontFamily.Ui");
        foreach (var block in Markdown.Parse(markdown, Pipeline)) {
            document.Blocks.Add(RenderBlock(block, openLink));
        }
        return document;
    }

    private static WpfBlock RenderBlock(Markdig.Syntax.Block block, Action<Uri> openLink)
    {
        if (block is ListBlock list) {
            var result = new System.Windows.Documents.List {
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Padding = new Thickness(18, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8)
            };
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) {
                result.StartIndex = start;
            }
            foreach (var item in list.OfType<ListItemBlock>()) {
                var entry = new ListItem { Margin = new Thickness(0, 0, 0, 3) };
                foreach (var child in item) {
                    entry.Blocks.Add(RenderBlock(child, openLink));
                }
                result.ListItems.Add(entry);
            }
            return result;
        }
        if (block is MarkdownTable table) {
            var result = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
            var group = new TableRowGroup();
            result.RowGroups.Add(group);
            foreach (var row in table.OfType<MarkdownTableRow>()) {
                var output = new TableRow { FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal };
                foreach (var cell in row.OfType<MarkdownTableCell>()) {
                    var outputCell = new TableCell { Padding = new Thickness(6), BorderThickness = new Thickness(0.5) };
                    outputCell.SetResourceReference(TableCell.BorderBrushProperty, "Brush.Border.Subtle");
                    foreach (var child in cell) {
                        outputCell.Blocks.Add(RenderBlock(child, openLink));
                    }
                    output.Cells.Add(outputCell);
                }
                group.Rows.Add(output);
            }
            return result;
        }
        if (block is ContainerBlock container) {
            var section = new Section { Margin = new Thickness(0, 0, 0, 8) };
            if (block is QuoteBlock) {
                section.BorderThickness = new Thickness(3, 0, 0, 0);
                section.Padding = new Thickness(10, 0, 0, 0);
                section.SetResourceReference(Section.BorderBrushProperty, "Brush.Primary.Border");
                section.SetResourceReference(Section.ForegroundProperty, "Brush.Text.Secondary");
            }
            foreach (var child in container) {
                section.Blocks.Add(RenderBlock(child, openLink));
            }
            return section;
        }
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 20 };
        if (block is CodeBlock code) {
            paragraph.Inlines.Add(new Run(code.Lines.ToString()));
            paragraph.FontSize = 12;
            paragraph.Padding = new Thickness(8);
            paragraph.SetResourceReference(Paragraph.FontFamilyProperty, "FontFamily.Monospace");
            paragraph.SetResourceReference(Paragraph.BackgroundProperty, "Brush.Surface.Disabled");
        } else if (block is ThematicBreakBlock) {
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "Brush.Border.Subtle");
        } else if (block is LeafBlock leaf) {
            if (leaf.Inline is not null) {
                AddInlines(paragraph.Inlines, leaf.Inline, openLink);
            } else {
                paragraph.Inlines.Add(new Run(leaf.Lines.ToString()));
            }
            if (block is HeadingBlock heading) {
                paragraph.FontSize = heading.Level switch { 1 => 18, 2 => 16, _ => 14 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 6, 0, 8);
            }
        }
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, ContainerInline source, Action<Uri> openLink)
    {
        foreach (var inline in source) {
            WpfInline rendered;
            switch (inline) {
                case LiteralInline literal:
                    rendered = new Run(literal.Content.ToString());
                    break;
                case CodeInline code:
                    rendered = new Run(code.Content);
                    rendered.SetResourceReference(TextElement.FontFamilyProperty, "FontFamily.Monospace");
                    rendered.SetResourceReference(TextElement.BackgroundProperty, "Brush.Surface.Disabled");
                    break;
                case LineBreakInline line:
                    rendered = line.IsHard ? new LineBreak() : new Run(" ");
                    break;
                case TaskList task:
                    rendered = new Run(task.Checked ? "☑ " : "☐ ");
                    break;
                case AutolinkInline auto:
                    rendered = Link(auto.Url, new Run(auto.Url), openLink);
                    break;
                case LinkInline link:
                    var label = new Span();
                    AddInlines(label.Inlines, link, openLink);
                    rendered = Link(link.Url, label, openLink);
                    break;
                case EmphasisInline emphasis:
                    var styled = new Span();
                    AddInlines(styled.Inlines, emphasis, openLink);
                    if (emphasis.DelimiterChar == '~') {
                        styled.TextDecorations = TextDecorations.Strikethrough;
                    } else if (emphasis.DelimiterCount == 2) {
                        styled.FontWeight = FontWeights.Bold;
                    } else {
                        styled.FontStyle = FontStyles.Italic;
                    }
                    rendered = styled;
                    break;
                case ContainerInline children:
                    var span = new Span();
                    AddInlines(span.Inlines, children, openLink);
                    rendered = span;
                    break;
                default:
                    continue;
            }
            target.Add(rendered);
        }
    }

    private static WpfInline Link(string? url, WpfInline label, Action<Uri> openLink)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) {
            return label;
        }
        var link = new Hyperlink(label) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        link.SetResourceReference(TextElement.ForegroundProperty, "Brush.Primary");
        link.RequestNavigate += (_, args) =>
        {
            openLink(uri);
            args.Handled = true;
        };
        return link;
    }
}
