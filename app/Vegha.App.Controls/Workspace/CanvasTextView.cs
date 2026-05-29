using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Read-only text viewer that paints visible lines directly via
/// <see cref="DrawingContext"/> — no per-row UI elements. Used for multi-MB response
/// bodies where AvaloniaEdit and even a virtualized ListBox stall on interaction.
/// Implements <see cref="ILogicalScrollable"/> so it lives inside a
/// <see cref="ScrollViewer"/> with logical scrolling (units are lines/chars). Owns
/// multi-line click-drag selection, Ctrl+A/Ctrl+C clipboard, and per-line JSON / XML
/// tokenization with a palette that mirrors <see cref="EditorSyntaxTheme"/>.
/// </summary>
public sealed class CanvasTextView : Control, ILogicalScrollable
{
    public enum Syntax { None, Json, Xml }

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CanvasTextView, string?>(
            nameof(Text), defaultValue: string.Empty);

    public static readonly StyledProperty<Syntax> SyntaxKindProperty =
        AvaloniaProperty.Register<CanvasTextView, Syntax>(nameof(SyntaxKind), Syntax.None);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<CanvasTextView>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<CanvasTextView>();

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Syntax SyntaxKind
    {
        get => GetValue(SyntaxKindProperty);
        set => SetValue(SyntaxKindProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    // -------------- backing state --------------
    private List<string> _lines = new();
    private int _maxLineChars;
    private CancellationTokenSource? _buildCts;
    private bool _isBuilding;
    private bool _suppressBoundsLoop;

    private double _lineHeight = 16;
    private double _charWidth = 8;
    private double _gutterWidth;
    private Typeface _typeface;

    // Theme-driven brushes — refreshed on every render so theme switches show up.
    private IBrush _bgBrush = Brushes.Transparent;
    private IBrush _fgBrush = Brushes.White;
    private IBrush _gutterBrush = Brushes.Gray;
    private IBrush _selBrush = new SolidColorBrush(Color.FromArgb(80, 100, 150, 200));

    // JSON / XML token brushes — assigned in RefreshBrushes().
    private IBrush _tokKey = Brushes.LightSkyBlue;
    private IBrush _tokString = Brushes.Salmon;
    private IBrush _tokNumber = Brushes.LightGreen;
    private IBrush _tokBoolNull = Brushes.CornflowerBlue;
    private IBrush _tokPunct = Brushes.LightGray;
    private IBrush _tokTag = Brushes.CornflowerBlue;
    private IBrush _tokAttr = Brushes.LightSkyBlue;
    private IBrush _tokAttrVal = Brushes.Salmon;
    private IBrush _tokComment = Brushes.YellowGreen;

    // Selection — Pos is (Line, Col); Col is char column in the source line.
    private struct Pos
    {
        public int Line;
        public int Col;
        public Pos(int line, int col) { Line = line; Col = col; }
        public static bool operator <(Pos a, Pos b) =>
            a.Line < b.Line || (a.Line == b.Line && a.Col < b.Col);
        public static bool operator >(Pos a, Pos b) => b < a;
        public static bool operator ==(Pos a, Pos b) => a.Line == b.Line && a.Col == b.Col;
        public static bool operator !=(Pos a, Pos b) => !(a == b);
        public override bool Equals(object? obj) => obj is Pos p && p == this;
        public override int GetHashCode() => HashCode.Combine(Line, Col);
    }

    private Pos? _selStart;
    private Pos? _selEnd;
    private Pos? _dragAnchor;
    private bool _dragging;

    // -------------- ILogicalScrollable --------------
    private Vector _offset;
    private Size _viewport;
    public event EventHandler? ScrollInvalidated;

    public bool CanHorizontallyScroll { get; set; } = true;
    public bool CanVerticallyScroll { get; set; } = true;
    public bool IsLogicalScrollEnabled => true;
    public Size ScrollSize => new(3, 1);
    public Size PageScrollSize => new(Math.Max(1, _viewport.Width - 3), Math.Max(1, _viewport.Height - 1));
    public Size Extent => new(_maxLineChars + GutterChars, _lines.Count);
    public Vector Offset
    {
        get => _offset;
        set
        {
            var clamped = ClampOffset(value);
            if (clamped == _offset) return;
            _offset = clamped;
            InvalidateVisual();
            ScrollInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }
    public Size Viewport => _viewport;
    public bool BringIntoView(Control target, Rect targetRect) => false;
    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    // -------------- ctor / lifecycle --------------
    public CanvasTextView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    static CanvasTextView()
    {
        AffectsRender<CanvasTextView>(TextProperty, SyntaxKindProperty,
            FontFamilyProperty, FontSizeProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // ILogicalScrollable children are sized to the ScrollContentPresenter's viewport
        // — return the constraint so we fill the visible area instead of the Control
        // default of (0,0). Clamp infinity to 0 for the rare case the host doesn't
        // constrain us (DataTemplate previews, design-time host).
        var w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Rebuild();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _typeface = default;
            MeasureFont();
            RecomputeGutter();
            InvalidateVisual();
            RaiseScrollInvalidated(EventArgs.Empty);
        }
        else if (change.Property == BoundsProperty)
        {
            // Re-entry guard: ScrollViewer can resize us in response to ScrollInvalidated,
            // which would fire BoundsProperty again. The flag breaks any layout loop.
            if (_suppressBoundsLoop) return;
            _suppressBoundsLoop = true;
            try
            {
                _viewport = new Size(
                    _charWidth > 0 ? Bounds.Width / _charWidth : 0,
                    _lineHeight > 0 ? Bounds.Height / _lineHeight : 0);
                _offset = ClampOffset(_offset);
                RaiseScrollInvalidated(EventArgs.Empty);
            }
            finally { _suppressBoundsLoop = false; }
        }
    }

    // -------------- model --------------
    private int GutterChars => Math.Max(3, _lines.Count.ToString(CultureInfo.InvariantCulture).Length) + 2;

    private void Rebuild()
    {
        // Cancel any in-flight rebuild — a newer response supersedes the old one.
        _buildCts?.Cancel();
        _buildCts = new CancellationTokenSource();
        var token = _buildCts.Token;
        var text = Text ?? string.Empty;

        // Reset selection + scroll synchronously so the UI doesn't show stale state
        // pointing at the previous response's lines while the new split is in flight.
        _selStart = _selEnd = _dragAnchor = null;
        _offset = default;

        if (text.Length == 0)
        {
            _lines = new List<string>();
            _maxLineChars = 0;
            _isBuilding = false;
            RecomputeGutter();
            InvalidateVisual();
            RaiseScrollInvalidated(EventArgs.Empty);
            return;
        }

        // Small bodies: split inline — a Task.Run hop costs more than the work itself.
        if (text.Length < 64 * 1024)
        {
            var (smallLines, smallMax) = SplitLines(text, token);
            _lines = smallLines;
            _maxLineChars = smallMax;
            _isBuilding = false;
            RecomputeGutter();
            InvalidateVisual();
            RaiseScrollInvalidated(EventArgs.Empty);
            return;
        }

        // Large bodies: split on a background thread so the UI thread stays responsive.
        // The viewer paints a "Preparing…" hint until the split lands.
        _isBuilding = true;
        InvalidateVisual();
        _ = SplitOffThreadAsync(text, token);
    }

    private async Task SplitOffThreadAsync(string text, CancellationToken token)
    {
        try
        {
            var (lines, max) = await Task.Run(() => SplitLines(text, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;
            _lines = lines;
            _maxLineChars = max;
            _isBuilding = false;
            RecomputeGutter();
            InvalidateVisual();
            RaiseScrollInvalidated(EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Superseded — drop silently.
        }
        catch
        {
            // Defensive — leave the viewer in a clean state rather than crashing.
            _isBuilding = false;
            InvalidateVisual();
        }
    }

    private static (List<string> Lines, int MaxChars) SplitLines(string text, CancellationToken token)
    {
        var lines = new List<string>(capacity: Math.Max(16, text.Length / 80));
        var max = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n') continue;
            var end = i;
            if (end > start && text[end - 1] == '\r') end--;
            var line = text.Substring(start, end - start);
            // Expand tabs lazily — pretty-printed JSON/XML use spaces so this is a
            // safety net rather than a hot path.
            if (line.IndexOf('\t') >= 0) line = line.Replace("\t", "    ");
            lines.Add(line);
            if (line.Length > max) max = line.Length;

            // Cooperative cancellation every ~8K lines so a rapid response swap
            // doesn't make us finish a stale split.
            if ((lines.Count & 0x1FFF) == 0) token.ThrowIfCancellationRequested();
            start = i + 1;
        }
        return (lines, max);
    }

    private void MeasureFont()
    {
        if (_typeface.FontFamily is null) _typeface = new Typeface(FontFamily);
        var probe = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _typeface, FontSize, Brushes.White);
        _charWidth = probe.Width > 0 ? probe.Width : 8;
        _lineHeight = probe.Height > 0 ? probe.Height : 16;
        _viewport = new Size(Bounds.Width / _charWidth, Bounds.Height / _lineHeight);
    }

    private void RecomputeGutter()
    {
        _gutterWidth = GutterChars * _charWidth + 8;
    }

    private Vector ClampOffset(Vector v)
    {
        var maxX = Math.Max(0, Extent.Width - _viewport.Width);
        var maxY = Math.Max(0, Extent.Height - _viewport.Height);
        var x = Math.Clamp(v.X, 0, maxX);
        var y = Math.Clamp(v.Y, 0, maxY);
        return new Vector(x, y);
    }

    // -------------- theme brushes --------------
    private void RefreshBrushes()
    {
        var isDark = IsDarkTheme();

        if (this.TryFindResource("CodeBgBrush", out var cb) && cb is IBrush cbBrush) _bgBrush = cbBrush;
        if (this.TryFindResource("Text0Brush", out var t0) && t0 is IBrush t0Brush) _fgBrush = t0Brush;
        if (this.TryFindResource("Text3Brush", out var t3) && t3 is IBrush t3Brush) _gutterBrush = t3Brush;
        if (this.TryFindResource("AccentBrush", out var ac) && ac is IBrush acBrush)
        {
            // Selection: 22% alpha of accent so the underlying text stays legible.
            if (acBrush is ISolidColorBrush solid)
                _selBrush = new SolidColorBrush(Color.FromArgb(0x38, solid.Color.R, solid.Color.G, solid.Color.B));
        }

        // Palette resolves the per-theme Code*Brush tokens (Themes/Tokens/*.axaml) so the
        // canvas large-text view matches EditorSyntaxTheme across every variant. The hex
        // fallback (used only if a token is missing) keeps the dark/light defaults safe.
        _tokKey       = CodeBrush("CodeAttrBrush",      isDark ? "#9CDCFE" : "#0070C9");
        _tokString    = CodeBrush("CodeStringBrush",    isDark ? "#CE9178" : "#A31515");
        _tokNumber    = CodeBrush("CodeNumberBrush",    isDark ? "#B5CEA8" : "#098658");
        _tokBoolNull  = CodeBrush("CodeBoolBrush",      isDark ? "#569CD6" : "#0070C9");
        _tokPunct     = CodeBrush("CodePunctBrush",     isDark ? "#D4D4D4" : "#15181D");
        _tokTag       = CodeBrush("CodeTagBrush",       isDark ? "#569CD6" : "#800000");
        _tokAttr      = CodeBrush("CodeAttrBrush",      isDark ? "#9CDCFE" : "#0070C9");
        _tokAttrVal   = CodeBrush("CodeAttrValueBrush", isDark ? "#CE9178" : "#0451A5");
        _tokComment   = CodeBrush("CodeCommentBrush",   isDark ? "#6A9955" : "#008000");
    }

    private static IBrush SolidHex(string hex) =>
        Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.White;

    /// <summary>Resolve a per-theme Code*Brush token from the control's resource scope,
    /// falling back to a fixed hex when the key is absent.</summary>
    private IBrush CodeBrush(string key, string fallbackHex) =>
        this.TryFindResource(key, out var r) && r is IBrush b ? b : SolidHex(fallbackHex);

    private bool IsDarkTheme()
    {
        var app = Application.Current;
        if (app is null) return true;
        return app.ActualThemeVariant == ThemeVariant.Dark
            || app.ActualThemeVariant == ThemeVariant.Default;
    }

    // -------------- render --------------
    public override void Render(DrawingContext context)
    {
        if (_lineHeight <= 0) MeasureFont();
        RefreshBrushes();

        context.FillRectangle(_bgBrush, new Rect(Bounds.Size));

        if (_isBuilding)
        {
            var hint = new FormattedText(
                "Preparing large response…",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                _typeface, FontSize, _gutterBrush);
            context.DrawText(hint,
                new Point((Bounds.Width - hint.Width) / 2, (Bounds.Height - hint.Height) / 2));
            return;
        }

        if (_lines.Count == 0) return;

        var firstVisible = (int)Math.Floor(_offset.Y);
        var visibleRows = (int)Math.Ceiling(Bounds.Height / _lineHeight) + 1;
        var lastVisible = Math.Min(_lines.Count - 1, firstVisible + visibleRows - 1);
        var xPixelOffset = -_offset.X * _charWidth;

        // Selection rectangles first so the text paints on top.
        DrawSelection(context, firstVisible, lastVisible, xPixelOffset);

        for (var i = firstVisible; i <= lastVisible; i++)
        {
            var y = (i - firstVisible) * _lineHeight - (_offset.Y - firstVisible) * _lineHeight;

            // Line-number gutter.
            var ln = (i + 1).ToString(CultureInfo.InvariantCulture);
            var lnText = new FormattedText(ln, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                _typeface, FontSize, _gutterBrush);
            var lnX = _gutterWidth - lnText.Width - 8;
            context.DrawText(lnText, new Point(lnX, y));

            // Body text (colorized when SyntaxKind is set). DrawLine clips to the visible
            // horizontal window so mega-lines never trigger full-line FormattedText measure.
            DrawLine(context, i, y);
        }
    }

    /// <summary>Above this line length we skip per-token colorization — scanning the
    /// whole line for tokens would be O(n) per visible-line per frame, and embedded
    /// HTML email templates / minified strings in JSON responses easily reach 100K+
    /// chars on a single line.</summary>
    private const int MaxLineCharsForHighlight = 4000;

    private void DrawLine(DrawingContext ctx, int lineIndex, double y)
    {
        var line = _lines[lineIndex];
        if (string.IsNullOrEmpty(line)) return;

        // Only build a FormattedText for the horizontally-visible substring. Without
        // this, a single 451K-char line in the response (a notification template
        // embedded as a JSON string value, for example) would freeze the UI for many
        // seconds on every paint — FormattedText measures every glyph.
        var firstCol = Math.Max(0, (int)Math.Floor(_offset.X));
        if (firstCol >= line.Length) return;

        var maxVisibleCols = (int)Math.Ceiling(_viewport.Width) + 4;
        var visibleLen = Math.Min(line.Length - firstCol, maxVisibleCols);
        var visible = line.Substring(firstCol, visibleLen);

        var ft = new FormattedText(visible, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _typeface, FontSize, _fgBrush);

        if (line.Length <= MaxLineCharsForHighlight)
        {
            switch (SyntaxKind)
            {
                case Syntax.Json: ColorizeJsonLine(ft, line, firstCol, visibleLen); break;
                case Syntax.Xml:  ColorizeXmlLine(ft, line, firstCol, visibleLen); break;
            }
        }

        // Position the substring at the correct column (handles fractional _offset.X).
        var x = _gutterWidth + (firstCol - _offset.X) * _charWidth;
        ctx.DrawText(ft, new Point(x, y));
    }

    private void DrawSelection(DrawingContext ctx, int firstVisible, int lastVisible, double xPixelOffset)
    {
        var sel = NormalizedSelection();
        if (sel is null) return;

        var (a, b) = sel.Value;
        var first = Math.Max(firstVisible, a.Line);
        var last = Math.Min(lastVisible, b.Line);

        for (var i = first; i <= last; i++)
        {
            var line = _lines[i];
            int startCol, endCol;
            if (i == a.Line && i == b.Line) { startCol = a.Col; endCol = b.Col; }
            else if (i == a.Line)            { startCol = a.Col; endCol = line.Length + 1; }
            else if (i == b.Line)            { startCol = 0;     endCol = b.Col; }
            else                              { startCol = 0;     endCol = line.Length + 1; }

            // +1 selects a sliver past EOL to communicate the line is fully picked
            // (and matches what most editors do for line-spanning selections).
            var y = (i - firstVisible) * _lineHeight - (_offset.Y - firstVisible) * _lineHeight;

            // Clamp the selection rect to the visible viewport so a 451K-char selected
            // line doesn't ask the renderer to fill a multi-million-pixel rectangle.
            var leftPx = _gutterWidth + xPixelOffset + startCol * _charWidth;
            var rightPx = _gutterWidth + xPixelOffset + endCol * _charWidth;
            leftPx = Math.Max(_gutterWidth, leftPx);
            rightPx = Math.Min(Bounds.Width, rightPx);
            if (rightPx > leftPx)
                ctx.FillRectangle(_selBrush, new Rect(leftPx, y, rightPx - leftPx, _lineHeight));
        }
    }

    private (Pos a, Pos b)? NormalizedSelection()
    {
        if (_selStart is null || _selEnd is null) return null;
        var s = _selStart.Value;
        var e = _selEnd.Value;
        if (s == e) return null;
        return s < e ? (s, e) : (e, s);
    }

    // -------------- pointer / selection --------------
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();

        var pos = HitTest(e.GetPosition(this));
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selStart is not null)
        {
            _selEnd = pos;
        }
        else
        {
            _selStart = pos;
            _selEnd = pos;
            _dragAnchor = pos;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        _selEnd = HitTest(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Mouse wheel scrolls vertically by 3 lines per click, horizontal when Shift held.
        var dx = e.Delta.X;
        var dy = e.Delta.Y;
        var step = 3.0;
        var newOffset = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            ? new Vector(_offset.X - dy * step, _offset.Y)
            : new Vector(_offset.X - dx * step, _offset.Y - dy * step);
        Offset = newOffset;
        e.Handled = true;
    }

    private Pos HitTest(Point p)
    {
        if (_lines.Count == 0) return new Pos(0, 0);
        var line = (int)Math.Floor(_offset.Y + p.Y / _lineHeight);
        line = Math.Clamp(line, 0, _lines.Count - 1);
        var contentX = p.X - _gutterWidth;
        var col = (int)Math.Round(_offset.X + contentX / _charWidth);
        if (col < 0) col = 0;
        var lineLen = _lines[line].Length;
        if (col > lineLen) col = lineLen;
        return new Pos(line, col);
    }

    // -------------- keyboard --------------
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
        }
    }

    private void SelectAll()
    {
        if (_lines.Count == 0) return;
        _selStart = new Pos(0, 0);
        _selEnd = new Pos(_lines.Count - 1, _lines[^1].Length);
        InvalidateVisual();
    }

    private async Task CopySelectionAsync()
    {
        var sel = NormalizedSelection();
        if (sel is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = ExtractSelection(sel.Value.a, sel.Value.b);
        try { await clipboard.SetTextAsync(text); }
        catch { /* clipboard contention — best-effort */ }
    }

    private string ExtractSelection(Pos a, Pos b)
    {
        if (a.Line == b.Line)
        {
            var line = _lines[a.Line];
            var len = Math.Min(b.Col, line.Length) - a.Col;
            return len > 0 ? line.Substring(a.Col, len) : string.Empty;
        }
        var sb = new StringBuilder();
        var startLine = _lines[a.Line];
        sb.Append(startLine, a.Col, Math.Max(0, startLine.Length - a.Col));
        sb.Append('\n');
        for (var i = a.Line + 1; i < b.Line; i++)
        {
            sb.Append(_lines[i]);
            sb.Append('\n');
        }
        var endLine = _lines[b.Line];
        sb.Append(endLine, 0, Math.Min(b.Col, endLine.Length));
        return sb.ToString();
    }

    // -------------- JSON tokenizer --------------
    // Per-line walker — fine for pretty-printed JSON where every key/value pair is
    // on its own line. Doesn't track cross-line string continuations, which JSON
    // forbids anyway (string literals can't contain unescaped newlines).
    //
    // viewStart..viewStart+viewLen is the visible substring rendered into the
    // FormattedText. SetForegroundBrush indices are relative to the visible
    // substring, so we translate token positions before applying colors and skip
    // entirely tokens that don't overlap the visible window. The scanner walks the
    // full line so its state (key-vs-string lookahead) stays correct, but exits
    // as soon as it passes the visible window.
    private void ColorizeJsonLine(FormattedText ft, string line, int viewStart, int viewLen)
    {
        var viewEnd = viewStart + viewLen;
        var i = 0;
        var n = line.Length;
        while (i < n)
        {
            if (i >= viewEnd) break;
            var c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '"')
            {
                var stringStart = i;
                var stringEnd = ScanJsonString(line, i);
                // Decide key vs string-value by looking ahead for ':' as the next
                // non-whitespace char.
                var afterClose = stringEnd + 1;
                var isKey = false;
                for (var j = afterClose; j < n; j++)
                {
                    var nc = line[j];
                    if (char.IsWhiteSpace(nc)) continue;
                    isKey = nc == ':';
                    break;
                }
                var brush = isKey ? _tokKey : _tokString;
                var len = Math.Min(stringEnd, n - 1) - stringStart + 1;
                ApplyVisible(ft, stringStart, len, viewStart, viewEnd, brush);
                i = stringEnd + 1;
                continue;
            }

            if (c == '{' || c == '}' || c == '[' || c == ']' || c == ',' || c == ':')
            {
                ApplyVisible(ft, i, 1, viewStart, viewEnd, _tokPunct);
                i++;
                continue;
            }

            // Numbers
            if (c == '-' || (c >= '0' && c <= '9'))
            {
                var numStart = i;
                while (i < n && IsJsonNumberChar(line[i])) i++;
                ApplyVisible(ft, numStart, i - numStart, viewStart, viewEnd, _tokNumber);
                continue;
            }

            // Literals
            if (TryMatch(line, i, "true") || TryMatch(line, i, "false"))
            {
                var len = line[i] == 't' ? 4 : 5;
                ApplyVisible(ft, i, len, viewStart, viewEnd, _tokBoolNull);
                i += len;
                continue;
            }
            if (TryMatch(line, i, "null"))
            {
                ApplyVisible(ft, i, 4, viewStart, viewEnd, _tokBoolNull);
                i += 4;
                continue;
            }

            i++;
        }
    }

    /// <summary>Apply <paramref name="brush"/> only to the portion of token
    /// [<paramref name="tokStart"/>, <paramref name="tokStart"/>+<paramref name="tokLen"/>)
    /// that falls inside the visible window [<paramref name="viewStart"/>, <paramref name="viewEnd"/>),
    /// with indices translated into the FormattedText's substring coordinate space.</summary>
    private static void ApplyVisible(FormattedText ft, int tokStart, int tokLen,
        int viewStart, int viewEnd, IBrush brush)
    {
        var tokEnd = tokStart + tokLen;
        if (tokEnd <= viewStart || tokStart >= viewEnd) return;
        var visStart = Math.Max(tokStart, viewStart) - viewStart;
        var visEnd = Math.Min(tokEnd, viewEnd) - viewStart;
        var visLen = visEnd - visStart;
        if (visLen > 0) ft.SetForegroundBrush(brush, visStart, visLen);
    }

    private static int ScanJsonString(string line, int start)
    {
        // start points at the opening '"'. Returns the index of the closing '"',
        // or the last char index if the line ends inside a string (clamp to EOL).
        var i = start + 1;
        var n = line.Length;
        while (i < n)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < n) { i += 2; continue; }
            if (c == '"') return i;
            i++;
        }
        return n - 1;
    }

    private static bool IsJsonNumberChar(char c) =>
        (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E';

    private static bool TryMatch(string s, int start, string token)
    {
        if (start + token.Length > s.Length) return false;
        for (var k = 0; k < token.Length; k++)
            if (s[start + k] != token[k]) return false;
        // Must be followed by a non-letter for word boundary (so "trueX" isn't matched).
        var after = start + token.Length;
        if (after < s.Length && char.IsLetterOrDigit(s[after])) return false;
        return true;
    }

    // -------------- XML tokenizer --------------
    // Per-line walker too. Multi-line comments / CDATA are colored only on the line
    // that contains the opening delimiter — pretty-printed XML keeps elements on
    // single lines so this matches what users see. Same visible-window scheme as
    // the JSON tokenizer.
    private void ColorizeXmlLine(FormattedText ft, string line, int viewStart, int viewLen)
    {
        var viewEnd = viewStart + viewLen;
        var i = 0;
        var n = line.Length;
        while (i < n)
        {
            if (i >= viewEnd) break;

            // Comment <!-- ... -->
            if (i + 3 < n && line[i] == '<' && line[i + 1] == '!' && line[i + 2] == '-' && line[i + 3] == '-')
            {
                var end = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var len = (end < 0 ? n : end + 3) - i;
                ApplyVisible(ft, i, len, viewStart, viewEnd, _tokComment);
                i += len;
                continue;
            }

            // Tag open
            if (line[i] == '<')
            {
                // <  or  </  followed by name
                ApplyVisible(ft, i, 1, viewStart, viewEnd, _tokPunct);
                i++;
                if (i < n && line[i] == '/')
                {
                    ApplyVisible(ft, i, 1, viewStart, viewEnd, _tokPunct);
                    i++;
                }
                // Tag name
                var nameStart = i;
                while (i < n && IsXmlNameChar(line[i])) i++;
                if (i > nameStart) ApplyVisible(ft, nameStart, i - nameStart, viewStart, viewEnd, _tokTag);

                // Attributes until '>' / '/>'
                while (i < n && line[i] != '>')
                {
                    if (char.IsWhiteSpace(line[i])) { i++; continue; }
                    if (line[i] == '/') { ApplyVisible(ft, i, 1, viewStart, viewEnd, _tokPunct); i++; continue; }

                    // Attribute name
                    var attrStart = i;
                    while (i < n && IsXmlNameChar(line[i])) i++;
                    if (i > attrStart) ApplyVisible(ft, attrStart, i - attrStart, viewStart, viewEnd, _tokAttr);

                    if (i < n && line[i] == '=') { ApplyVisible(ft, i, 1, viewStart, viewEnd, _tokPunct); i++; }

                    // Quoted value
                    if (i < n && (line[i] == '"' || line[i] == '\''))
                    {
                        var quote = line[i];
                        var valStart = i;
                        i++;
                        while (i < n && line[i] != quote) i++;
                        var valEnd = Math.Min(n - 1, i);
                        ApplyVisible(ft, valStart, valEnd - valStart + 1, viewStart, viewEnd, _tokAttrVal);
                        if (i < n) i++;
                    }
                    else
                    {
                        // Malformed / unquoted — advance one char to make progress.
                        i++;
                    }
                }
                if (i < n && line[i] == '>') { ApplyVisible(ft, i, 1, viewStart, viewEnd, _tokPunct); i++; }
                continue;
            }

            // Default (text content) — already painted in foreground brush.
            i++;
        }
    }

    private static bool IsXmlNameChar(char c) =>
        char.IsLetterOrDigit(c) || c == ':' || c == '-' || c == '_' || c == '.';
}
