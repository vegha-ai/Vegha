using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using AvaloniaInline = Avalonia.Controls.Documents.Inline;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// A read-only Markdown renderer driven by Markdig. Walks the parsed document tree and
/// emits Avalonia primitives (TextBlock, SelectableTextBlock for code blocks, Border-wrapped
/// blockquotes). Supports headings, paragraphs, code spans + fenced code, lists, links, and
/// inline emphasis. Tables and HTML pass-throughs render as plain text.
/// </summary>
public partial class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly MarkdownPipeline s_pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownView()
    {
        InitializeComponent();
        // Avalonia 11 uses System.IObserver<T> here, not Rx; use the AddClassHandler pattern.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == MarkdownProperty) Render();
        };
        AttachedToVisualTree += (_, _) => Render();
    }

    private void Render()
    {
        if (this.FindControl<StackPanel>("Body") is not { } body) return;
        body.Children.Clear();
        var src = Markdown ?? string.Empty;
        if (string.IsNullOrWhiteSpace(src))
        {
            body.Children.Add(new TextBlock
            {
                Text = "(empty — write Markdown on the left)",
                Foreground = TryBrush("Text3Brush"),
                FontStyle = FontStyle.Italic,
                FontSize = 11,
            });
            return;
        }
        var doc = Markdig.Markdown.Parse(src, s_pipeline);
        foreach (var block in doc) body.Children.Add(RenderBlock(block));
    }

    private Control RenderBlock(Block block) => block switch
    {
        HeadingBlock heading => MakeHeading(heading),
        ParagraphBlock para => MakeInlineBlock(para.Inline, fontSize: 12),
        FencedCodeBlock code => MakeCodeBlock(code.Lines.ToString()),
        CodeBlock code => MakeCodeBlock(code.Lines.ToString()),
        QuoteBlock quote => MakeQuoteBlock(quote),
        ListBlock list => MakeList(list),
        ThematicBreakBlock => new Border
        {
            Height = 1,
            Background = TryBrush("Border0Brush"),
            Margin = new Thickness(0, 6, 0, 6),
        },
        _ => new TextBlock { Text = block.ToString() ?? string.Empty, FontSize = 11 },
    };

    private Control MakeHeading(HeadingBlock heading)
    {
        var size = heading.Level switch
        {
            1 => 18.0, 2 => 16.0, 3 => 14.0, 4 => 13.0, _ => 12.0,
        };
        var tb = MakeInlineBlock(heading.Inline, fontSize: size);
        if (tb is TextBlock tbl)
        {
            tbl.FontWeight = FontWeight.SemiBold;
            tbl.Margin = new Thickness(0, 8, 0, 4);
        }
        return tb;
    }

    private TextBlock MakeInlineBlock(ContainerInline? inline, double fontSize)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            Foreground = TryBrush("Text0Brush"),
            TextWrapping = TextWrapping.Wrap,
        };
        if (inline is null) return tb;
        foreach (var run in InlineRuns(inline)) tb.Inlines!.Add(run);
        return tb;
    }

    private IEnumerable<AvaloniaInline> InlineRuns(ContainerInline container)
    {
        foreach (var item in container)
        {
            switch (item)
            {
                case LiteralInline lit:
                    yield return new Run(lit.Content.ToString());
                    break;
                case CodeInline code:
                    yield return new Run(code.Content)
                    {
                        FontFamily = TryFont("MonoFont"),
                        Background = TryBrush("Bg3Brush"),
                    };
                    break;
                case EmphasisInline em:
                    var span = new Span();
                    foreach (var inner in InlineRuns(em)) span.Inlines.Add(inner);
                    span.FontWeight = em.DelimiterCount >= 2 ? FontWeight.Bold : FontWeight.Normal;
                    span.FontStyle = em.DelimiterCount == 1 ? FontStyle.Italic : FontStyle.Normal;
                    yield return span;
                    break;
                case LinkInline link:
                    var label = string.Concat(InlineRuns(link).OfType<Run>().Select(r => r.Text));
                    yield return new Run(string.IsNullOrEmpty(label) ? (link.Url ?? string.Empty) : label)
                    {
                        Foreground = TryBrush("AccentBrush"),
                        TextDecorations = TextDecorations.Underline,
                    };
                    break;
                case LineBreakInline:
                    yield return new LineBreak();
                    break;
                default:
                    yield return new Run(item.ToString() ?? string.Empty);
                    break;
            }
        }
    }

    private Control MakeCodeBlock(string text) => new Border
    {
        Background = TryBrush("Bg3Brush"),
        Padding = new Thickness(10, 6),
        CornerRadius = new CornerRadius(3),
        Margin = new Thickness(0, 4, 0, 4),
        Child = new SelectableTextBlock
        {
            Text = text.TrimEnd(),
            FontFamily = TryFont("MonoFont"),
            FontSize = 11,
            Foreground = TryBrush("Text0Brush"),
            TextWrapping = TextWrapping.NoWrap,
        },
    };

    private Control MakeQuoteBlock(QuoteBlock quote)
    {
        var inner = new StackPanel { Spacing = 4 };
        foreach (var b in quote) inner.Children.Add(RenderBlock(b));
        return new Border
        {
            BorderBrush = TryBrush("Border1Brush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 4),
            Child = inner,
        };
    }

    private Control MakeList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 2 };
        var ordered = list.IsOrdered;
        var index = 1;
        foreach (var li in list.OfType<ListItemBlock>())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = ordered ? $"{index}." : "•",
                FontSize = 12,
                Foreground = TryBrush("Text2Brush"),
                MinWidth = 16,
            });
            var inner = new StackPanel();
            foreach (var b in li) inner.Children.Add(RenderBlock(b));
            row.Children.Add(inner);
            panel.Children.Add(row);
            index++;
        }
        return panel;
    }

    private IBrush TryBrush(string key) =>
        this.TryFindResource(key, out var b) && b is IBrush brush ? brush : new SolidColorBrush(Colors.Gray);

    private FontFamily TryFont(string key) =>
        this.TryFindResource(key, out var f) && f is FontFamily font ? font : FontFamily.Default;
}
