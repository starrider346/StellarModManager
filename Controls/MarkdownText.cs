using System;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace StellarModManager.Controls;

public sealed class MarkdownText : Decorator
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
    private static readonly FontFamily CodeFont = new("Consolas, Menlo, DejaVu Sans Mono");

    public static readonly IValueConverter Preview = new FuncValueConverter<string, string>(source =>
        string.IsNullOrWhiteSpace(source) ? "" : Regex.Replace(Markdown.ToPlainText(source, Pipeline), @"\s+", " ").Trim());

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<MarkdownText, string?>(nameof(Source));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
            Child = Source is { } source && !string.IsNullOrWhiteSpace(source)
                ? RenderBlocks(Markdown.Parse(source, Pipeline)) : null;
    }

    private static StackPanel RenderBlocks(ContainerBlock blocks, double spacing = 8)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var block in blocks)
        {
            Control? content = block switch
            {
                HeadingBlock heading => RenderLeaf(heading, Math.Max(14, 21 - heading.Level), FontWeight.SemiBold),
                ParagraphBlock paragraph => RenderLeaf(paragraph),
                CodeBlock code => new Border
                {
                    Classes = { "markdownCode" },
                    Child = new SelectableTextBlock
                    {
                        FontFamily = CodeFont, TextWrapping = TextWrapping.Wrap, Text = code.Lines.ToString()
                    }
                },
                ListBlock list => RenderList(list),
                QuoteBlock quote => new Border { Classes = { "markdownQuote" }, Child = RenderBlocks(quote) },
                ThematicBreakBlock => new Border { Classes = { "markdownRule" } },
                _ => null
            };
            if (content != null)
                panel.Children.Add(content);
        }

        return panel;
    }

    private static SelectableTextBlock RenderLeaf(LeafBlock block, double size = 14, FontWeight weight = FontWeight.Normal)
    {
        var inlines = new InlineCollection();
        AppendInlines(inlines, block.Inline);
        return new SelectableTextBlock
        {
            Classes = { "markdownParagraph" }, FontSize = size, FontWeight = weight,
            TextWrapping = TextWrapping.Wrap, Inlines = inlines
        };
    }

    private static StackPanel RenderList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = list.IsLoose ? 8 : 3 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock
            {
                Classes = { "markdownMarker" }, Text = list.IsOrdered ? $"{number++}." : "•"
            });
            var body = RenderBlocks(item);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static void AppendInlines(InlineCollection target, ContainerInline? inlines)
    {
        if (inlines == null)
            return;

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = CodeFont });
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    AppendInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case LinkInline { IsImage: false } link when WebUri(link.Url) is { } destination:
                    var label = new InlineCollection();
                    AppendInlines(label, link);
                    target.Add(CreateLink(destination, new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 240, Inlines = label }));
                    break;
                case AutolinkInline autoLink:
                    target.Add(WebUri(autoLink.Url) is { } webLink
                        ? CreateLink(webLink, new TextBlock { Text = autoLink.Url, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 })
                        : new Run(autoLink.Url));
                    break;
                case ContainerInline nested:
                    AppendInlines(target, nested);
                    break;
            }
        }
    }

    private static Uri? WebUri(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var destination) && destination.Scheme is "http" or "https"
            ? destination : null;

    private static InlineUIContainer CreateLink(Uri destination, TextBlock label)
    {
        var link = new HyperlinkButton { Classes = { "markdownLink" }, Content = label, NavigateUri = destination };
        ToolTip.SetTip(link, destination.AbsoluteUri);
        return new InlineUIContainer { Child = link, BaselineAlignment = BaselineAlignment.TextBottom };
    }
}
