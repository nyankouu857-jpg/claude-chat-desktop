using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace ClaudeChat.Native.Views;

// Markdig の構文木を WPF の FlowDocument に組み替える。Electron 版でいう marked と highlight.js の役
public static class MarkdownRenderer
{
    // チャットの改行をそのまま改行として見せたいので softline を hardline 扱いに
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    static readonly double[] HeadingSizes = [22, 19, 17, 16, 15, 15];

    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        document.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        document.LineHeight = 25;
        foreach (var block in Markdown.Parse(markdown, Pipeline)) AddBlock(document.Blocks, block);
        return document;
    }

    static void AddBlock(BlockCollection target, MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var paragraph = new Paragraph { FontSize = HeadingSizes[Math.Clamp(heading.Level - 1, 0, 5)], FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 6) };
                AddInlines(paragraph.Inlines, heading.Inline);
                target.Add(paragraph);
                break;
            }
            case ParagraphBlock paragraphBlock:
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 6, 0, 6) };
                AddInlines(paragraph.Inlines, paragraphBlock.Inline);
                target.Add(paragraph);
                break;
            }
            case ListBlock listBlock:
            {
                var list = new List
                {
                    MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 4, 0, 4),
                    Padding = new Thickness(24, 0, 0, 0),
                };
                if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, out var start)) list.StartIndex = start;
                foreach (var item in listBlock.OfType<ListItemBlock>())
                {
                    var listItem = new ListItem();
                    foreach (var child in item) AddBlock(listItem.Blocks, child);
                    // 箇条書きの中の段落に余白があると行間が間延びする
                    foreach (var p in listItem.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0, 2, 0, 2);
                    list.ListItems.Add(listItem);
                }
                target.Add(list);
                break;
            }
            case CodeBlock codeBlock:
                AddCodeBlock(target, codeBlock);
                break;
            case QuoteBlock quote:
            {
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(0, 6, 0, 6) };
                section.SetResourceReference(Section.BorderBrushProperty, "LineBrush");
                section.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
                foreach (var child in quote) AddBlock(section.Blocks, child);
                target.Add(section);
                break;
            }
            case ThematicBreakBlock:
            {
                var rule = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 10, 0, 10), FontSize = 1 };
                rule.SetResourceReference(Paragraph.BorderBrushProperty, "LineBrush");
                target.Add(rule);
                break;
            }
            case Markdig.Extensions.Tables.Table table:
                target.Add(BuildTable(table));
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(target, child);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
            {
                var paragraph = new Paragraph();
                AddInlines(paragraph.Inlines, leaf.Inline);
                target.Add(paragraph);
                break;
            }
        }
    }

    static void AddCodeBlock(BlockCollection target, CodeBlock codeBlock)
    {
        var language = (codeBlock as FencedCodeBlock)?.Info ?? "";
        var code = codeBlock.Lines.ToString().TrimEnd('\n', '\r');

        var copyLink = new Hyperlink(new Run("コピー")) { TextDecorations = null };
        copyLink.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
        copyLink.Click += (_, _) => Clipboard.SetText(code);
        var header = new Paragraph { Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(14, 6, 14, 0), FontSize = 12, TextAlignment = TextAlignment.Right };
        header.SetResourceReference(Paragraph.BackgroundProperty, "CodeBgBrush");
        header.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
        if (language.Length > 0) header.Inlines.Add(new Run(language + "   "));
        header.Inlines.Add(copyLink);
        target.Add(header);

        var body = new Paragraph { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(14, 4, 14, 12), FontSize = 13, LineHeight = 20 };
        body.SetResourceReference(Paragraph.BackgroundProperty, "CodeBgBrush");
        body.SetResourceReference(TextElement.FontFamilyProperty, "CodeFont");
        foreach (var (text, kind) in CodeHighlighter.Tokenize(code, language))
        {
            var run = new Run(text);
            var brushKey = kind switch
            {
                TokenKind.Keyword => "SyntaxKeywordBrush",
                TokenKind.String => "SyntaxStringBrush",
                TokenKind.Comment => "SyntaxCommentBrush",
                TokenKind.Number => "SyntaxNumberBrush",
                _ => null,
            };
            if (brushKey is not null) run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            body.Inlines.Add(run);
        }
        target.Add(body);
    }

    static WpfTable BuildTable(Markdig.Extensions.Tables.Table table)
    {
        var result = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 6) };
        var group = new TableRowGroup();
        foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var tableRow = new System.Windows.Documents.TableRow();
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var tableCell = new System.Windows.Documents.TableCell { BorderThickness = new Thickness(0.5), Padding = new Thickness(8, 3, 8, 3) };
                tableCell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "LineBrush");
                foreach (var child in cell) AddBlock(tableCell.Blocks, child);
                if (row.IsHeader) tableCell.FontWeight = FontWeights.Bold;
                tableRow.Cells.Add(tableCell);
            }
            group.Rows.Add(tableRow);
        }
        result.RowGroups.Add(group);
        return result;
    }

    static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container) AddInline(target, inline);
    }

    static void AddInline(InlineCollection target, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontSize = 13.5 };
                run.SetResourceReference(TextElement.FontFamilyProperty, "CodeFont");
                run.SetResourceReference(TextElement.BackgroundProperty, "CodeBgBrush");
                target.Add(run);
                break;
            }
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case EmphasisInline emphasis:
            {
                Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold()
                    : new Italic();
                AddInlines(span.Inlines, emphasis);
                target.Add(span);
                break;
            }
            case LinkInline { IsImage: false } link:
            {
                var hyperlink = CreateHyperlink(link.Url);
                AddInlines(hyperlink.Inlines, link);
                target.Add(hyperlink);
                break;
            }
            case AutolinkInline autolink:
            {
                var hyperlink = CreateHyperlink(autolink.Url);
                hyperlink.Inlines.Add(new Run(autolink.Url));
                target.Add(hyperlink);
                break;
            }
            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;
            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;
            case ContainerInline container:
                AddInlines(target, container);
                break;
        }
    }

    public static Hyperlink CreateHyperlink(string? url)
    {
        var hyperlink = new Hyperlink();
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.ToolTip = uri.ToString();
            hyperlink.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        return hyperlink;
    }
}
