using System.Windows;
using System.Windows.Documents;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownBlock = Markdig.Syntax.Block;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;

namespace OrgExplorer.Desktop;

internal static class PrivateMarkdownReading
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    internal static FlowDocument Create(string markdown, Action<Uri> openLink)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0), FontSize = 14,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        };
        foreach (var block in Markdown.Parse(markdown, Pipeline)) AddBlock(document.Blocks, block, openLink);
        return document;
    }

    private static void AddBlock(BlockCollection output, MarkdownBlock block, Action<Uri> openLink)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var paragraph = new Paragraph { FontSize = 28 - heading.Level * 2, FontWeight = FontWeights.Bold };
                AddInlines(paragraph.Inlines, heading.Inline, openLink);
                output.Add(paragraph);
                break;
            case ParagraphBlock text:
                var body = new Paragraph();
                AddInlines(body.Inlines, text.Inline, openLink);
                output.Add(body);
                break;
            case CodeBlock code:
                output.Add(new Paragraph(new Run(code.Lines.ToString()))
                    { FontFamily = new System.Windows.Media.FontFamily("Consolas") });
                break;
            case ListBlock list:
                var rendered = new System.Windows.Documents.List
                    { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc };
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var entry = new ListItem();
                    foreach (var child in item) AddBlock(entry.Blocks, child, openLink);
                    rendered.ListItems.Add(entry);
                }
                output.Add(rendered);
                break;
            case QuoteBlock quote:
                var section = new Section { Margin = new Thickness(16, 0, 0, 0), FontStyle = FontStyles.Italic };
                foreach (var child in quote) AddBlock(section.Blocks, child, openLink);
                output.Add(section);
                break;
            case ThematicBreakBlock:
                output.Add(new Paragraph(new Run("────────")));
                break;
        }
    }

    private static void AddInlines(InlineCollection output, ContainerInline? container, Action<Uri> openLink)
    {
        if (container is null) return;
        foreach (var inline in container) AddInline(output, inline, openLink);
    }

    private static void AddInline(InlineCollection output, MarkdownInline inline, Action<Uri> openLink)
    {
        switch (inline)
        {
            case LiteralInline literal:
                output.Add(new Run(literal.Content.ToString()));
                break;
            case HtmlEntityInline entity:
                output.Add(new Run(entity.Transcoded.ToString()));
                break;
            case CodeInline code:
                output.Add(new Run(code.Content) { FontFamily = new System.Windows.Media.FontFamily("Consolas") });
                break;
            case LineBreakInline:
                output.Add(new LineBreak());
                break;
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(span.Inlines, emphasis, openLink);
                output.Add(span);
                break;
            case LinkInline { IsImage: true } image:
                output.Add(new Run("[Image not loaded: "));
                AddInlines(output, image, openLink);
                output.Add(new Run("]"));
                break;
            case LinkInline link:
                var linkSpan = ExternalLink(link.Url, openLink);
                AddInlines(linkSpan.Inlines, link, openLink);
                output.Add(linkSpan);
                break;
            case AutolinkInline link:
                var auto = ExternalLink(link.Url, openLink);
                auto.Inlines.Add(new Run(link.Url));
                output.Add(auto);
                break;
            case ContainerInline nested:
                AddInlines(output, nested, openLink);
                break;
        }
    }

    private static Span ExternalLink(string? value, Action<Uri> openLink)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return new Span { ToolTip = "Only explicit HTTP/HTTPS external links can be opened." };
        var link = new Hyperlink { ToolTip = "Open externally: " + uri.AbsoluteUri };
        link.Click += (_, _) => openLink(uri);
        return link;
    }
}
