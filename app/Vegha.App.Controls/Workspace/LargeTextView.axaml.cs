using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Virtualized read-only viewer used for response bodies that are too large for
/// AvaloniaEdit. Splits the input text into lines once on every Text change and
/// hosts them in a <see cref="ListBox"/> with <see cref="VirtualizingStackPanel"/>
/// (Avalonia's default for ListBox), so only the visible rows ever realize
/// regardless of total line count. Each row puts the text into a
/// <see cref="SelectableTextBlock"/> so the user can click-and-drag to select
/// within a line and Ctrl+C to copy; the ListBoxItem's selection visual is erased
/// in the XAML so the row never paints a highlight.
/// Long lines (e.g. a 16 MB minified JSON on a single line) are chunked so the
/// per-row text never has to measure megabytes of content.
/// </summary>
public partial class LargeTextView : UserControl
{
    /// <summary>Maximum characters per rendered row. Lines longer than this are chunked
    /// into multiple rows; only the first row carries the line number. Keeps per-row
    /// text measurement bounded — without this a single-line 16 MB body would
    /// produce one massive TextBlock and defeat virtualization entirely.</summary>
    private const int MaxCharsPerRow = 2000;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<LargeTextView, string?>(
            nameof(Text), defaultValue: string.Empty);

    public static readonly DirectProperty<LargeTextView, bool> IsBuildingProperty =
        AvaloniaProperty.RegisterDirect<LargeTextView, bool>(
            nameof(IsBuilding), o => o.IsBuilding);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private bool _isBuilding;
    /// <summary>True while a background pass is splitting the body into row records.
    /// The XAML hides the ListBox and shows a "Preparing…" hint while this is true.</summary>
    public bool IsBuilding
    {
        get => _isBuilding;
        private set => SetAndRaise(IsBuildingProperty, ref _isBuilding, value);
    }

    private ListBox _list = null!;
    private CancellationTokenSource? _buildCts;

    public LargeTextView()
    {
        InitializeComponent();
        _list = this.FindControl<ListBox>("LineList")!;
        // Selection + Ctrl+C are owned by the per-row SelectableTextBlock inside the
        // item template; the ListBoxItem's visual selection state is erased in XAML
        // so the row never paints a highlight even when clicked.
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            RebuildAsync(change.GetNewValue<string?>());
    }

    private async void RebuildAsync(string? text)
    {
        // Cancel any in-flight build — the user could trigger a new response before
        // the previous one finished chunking.
        _buildCts?.Cancel();
        _buildCts = new CancellationTokenSource();
        var token = _buildCts.Token;

        if (string.IsNullOrEmpty(text))
        {
            _list.ItemsSource = Array.Empty<LineRow>();
            IsBuilding = false;
            return;
        }

        // Tiny bodies don't need a background pass — splitting a few KB inline is
        // cheaper than a Task.Run hop and avoids the "Preparing…" flash.
        if (text.Length < 64 * 1024)
        {
            _list.ItemsSource = BuildLines(text, token);
            IsBuilding = false;
            return;
        }

        IsBuilding = true;
        try
        {
            var items = await Task.Run(() => BuildLines(text, token), token);
            if (token.IsCancellationRequested) return;
            _list.ItemsSource = items;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Text change — drop silently.
        }
        finally
        {
            if (!token.IsCancellationRequested) IsBuilding = false;
        }
    }

    private static IReadOnlyList<LineRow> BuildLines(string text, CancellationToken token)
    {
        // Single allocation pass: scan for \n boundaries, emit one or more rows per
        // logical line (chunked when the line exceeds MaxCharsPerRow).
        var result = new List<LineRow>(capacity: Math.Max(64, text.Length / 80));
        var lineNum = 1;
        var lineStart = 0;
        var len = text.Length;
        for (var i = 0; i <= len; i++)
        {
            if (i != len && text[i] != '\n') continue;

            // Strip a trailing \r so we don't render a stray glyph on CRLF inputs.
            var end = i;
            if (end > lineStart && text[end - 1] == '\r') end--;
            var lineLen = end - lineStart;

            if (lineLen <= MaxCharsPerRow)
            {
                result.Add(new LineRow(lineNum, text.Substring(lineStart, lineLen)));
            }
            else
            {
                var offset = lineStart;
                var remaining = lineLen;
                var first = true;
                while (remaining > 0)
                {
                    var take = Math.Min(MaxCharsPerRow, remaining);
                    // Subsequent chunks of the same logical line carry no number so the
                    // gutter doesn't repeat — visually they read as a soft-wrapped tail.
                    result.Add(new LineRow(first ? lineNum : 0, text.Substring(offset, take)));
                    offset += take;
                    remaining -= take;
                    first = false;
                }
            }

            // Cooperative cancellation every ~10K lines keeps responsiveness when a
            // newer Text replaces the in-flight build.
            if ((lineNum & 0x1FFF) == 0) token.ThrowIfCancellationRequested();
            lineNum++;
            lineStart = i + 1;
        }
        return result;
    }

}

/// <summary>One row in the virtualized line list. <see cref="Number"/> is 0 for
/// continuation chunks of a long line (the gutter renders blank) so a single
/// huge line splits visually into multiple rows under the same line number.</summary>
public sealed record LineRow(int Number, string Text)
{
    public string Display => Number > 0 ? Number.ToString() : string.Empty;
}
