using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using Vegha.App.Controls.Icons;
using Vegha.Core.Scripting;
using CoreScriptKind = Vegha.Core.Scripting.ScriptKind;

namespace Vegha.App.Controls.Workspace;

/// <summary>Autocomplete behavior for a <see cref="VariableAwareTextEditor"/>.</summary>
public enum EditorCompletionMode
{
    /// <summary>No autocomplete.</summary>
    None,
    /// <summary><c>{{var}}</c> popup on typing <c>{{</c> (URL / KV value fields).</summary>
    Variables,
    /// <summary>JavaScript object/member autocomplete + syntax squiggles (script editors).</summary>
    Script,
}

/// <summary>
/// Text editor that highlights <c>{{name}}</c> variable tokens, shows their resolved values on hover,
/// and offers an autocomplete popup when the user types <c>{{</c>. Used for URL fields and any other
/// place where variable interpolation is supported.
/// </summary>
public partial class VariableAwareTextEditor : UserControl
{
    private static readonly Regex VarRegex = new(@"\{\{([^{}]*?)\}\}", RegexOptions.Compiled);

    // Resolved variable — amber. Slightly brighter in dark mode so the
    // {{var}} tokens stay legible against #0a0c0f / similar code backgrounds.
    private static readonly IBrush VariableBrushDark = new SolidColorBrush(Color.Parse("#FBBF24"));  // amber-400
    private static readonly IBrush VariableBrushLight = new SolidColorBrush(Color.Parse("#D97706")); // amber-600

    // Unresolved variable — red. The previous #EF4444 (red-500) reads as
    // dim maroon on dark code backgrounds; bump to a much lighter pinkish red
    // so missing variables stand out without straining the eye.
    private static readonly IBrush UnresolvedBrushDark = new SolidColorBrush(Color.Parse("#FCA5A5"));  // red-300
    private static readonly IBrush UnresolvedBrushLight = new SolidColorBrush(Color.Parse("#DC2626")); // red-600

    private static IBrush VariableBrush =>
        IsDarkVariant() ? VariableBrushDark : VariableBrushLight;
    private static IBrush UnresolvedBrush =>
        IsDarkVariant() ? UnresolvedBrushDark : UnresolvedBrushLight;

    private static bool IsDarkVariant()
    {
        var v = global::Avalonia.Application.Current?.ActualThemeVariant;
        return v == global::Avalonia.Styling.ThemeVariant.Dark
            || v == global::Avalonia.Styling.ThemeVariant.Default;
    }

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, string>(
            nameof(Text), defaultValue: string.Empty,
            defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Snapshot of variables the editor uses for hover tooltips and autocomplete suggestions.
    /// Caller should refresh this when env/request vars change; the colorizer reads it on each render.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> VariablesProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, IReadOnlyDictionary<string, string>?>(
            nameof(Variables));

    /// <summary>If true, the editor strips line breaks on input/paste (URL field mode).</summary>
    public static readonly StyledProperty<bool> SingleLineProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, bool>(
            nameof(SingleLine), defaultValue: true);

    /// <summary>Watermark shown when the editor is empty.</summary>
    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, string?>(nameof(Watermark));

    /// <summary>Optional AvaloniaEdit syntax-highlighting definition name (e.g. "JavaScript",
    /// "MarkDown", "JSON"). Empty string disables built-in highlighting; <c>{{var}}</c>
    /// colorization stays on independently.</summary>
    public static readonly StyledProperty<string?> SyntaxHighlightingNameProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, string?>(nameof(SyntaxHighlightingName));

    /// <summary>Show line numbers (script editors). Default false (URL field default).</summary>
    public static readonly StyledProperty<bool> ShowLineNumbersProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, bool>(nameof(ShowLineNumbers), defaultValue: false);

    /// <summary>Render the surrounding box (rounded border + input background). Default true so
    /// single-line URL/value cells keep their familiar boxed look. Set false on multi-line code
    /// surfaces (body, scripts) where the host already paints the background and a box would
    /// just add visual noise.</summary>
    public static readonly StyledProperty<bool> BorderedProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, bool>(nameof(Bordered), defaultValue: true);

    /// <summary>Per-instance word-wrap toggle for multi-line editors. Default <c>true</c> —
    /// long lines wrap to the viewport width. Set <c>false</c> on instances where wrap
    /// would obscure content (e.g. a SOAP raw editor where every angle bracket matters).
    /// Single-line editors (URL / KV value cells) ignore this — they always force wrap off
    /// regardless. Replaces the global <c>EditorWordWrap</c> app resource which couldn't
    /// be overridden per-editor.</summary>
    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, bool>(nameof(WordWrap), defaultValue: true);

    /// <summary>Renders non-<c>{{var}}</c> characters as bullet glyphs so the field behaves like a
    /// password input, while leaving <c>{{var}}</c> placeholders visible so the user can see which
    /// variable will be substituted. The underlying document keeps the real text intact for binding;
    /// only the rendering is masked. When <see cref="IsRevealed"/> is true the mask is bypassed.</summary>
    public static readonly StyledProperty<bool> IsPasswordProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, bool>(nameof(IsPassword), defaultValue: false);

    /// <summary>Toggles the password mask off so the plaintext is shown. Driven by the inline eye
    /// toggle that appears when <see cref="IsPassword"/> is true; can also be bound externally to
    /// share reveal state across multiple editors.</summary>
    public static readonly StyledProperty<bool> IsRevealedProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, bool>(
            nameof(IsRevealed), defaultValue: false,
            defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Optional advisory shown as a hover tooltip on an inline warning glyph next to the
    /// reveal eye. Set to a non-empty string to surface a passive note (e.g. "Stored as plaintext
    /// on disk — consider <c>{{vault:…}}</c> references."). Null/empty hides the warning glyph.</summary>
    public static readonly StyledProperty<string?> WarningTipProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, string?>(nameof(WarningTip));

    /// <summary>Autocomplete behavior. Default <see cref="EditorCompletionMode.Variables"/> keeps
    /// the existing <c>{{var}}</c> popup for URL/KV fields; script editors set
    /// <see cref="EditorCompletionMode.Script"/> for JS object/member completion + squiggles.</summary>
    public static readonly StyledProperty<EditorCompletionMode> CompletionModeProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, EditorCompletionMode>(
            nameof(CompletionMode), defaultValue: EditorCompletionMode.Variables);

    /// <summary>Which script slot this editor edits — gates which globals (e.g. <c>res</c>) the
    /// script autocomplete offers. Only meaningful when <see cref="CompletionMode"/> is Script.</summary>
    public static readonly StyledProperty<CoreScriptKind> ScriptKindProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, CoreScriptKind>(
            nameof(ScriptKind), defaultValue: CoreScriptKind.PreRequest);

    /// <summary>First syntax error as a one-line summary (e.g. "Line 3, col 8: …"), or null when
    /// clean. Set internally as diagnostics update; hosts bind a small error strip to it.</summary>
    public static readonly StyledProperty<string?> ErrorSummaryProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, string?>(nameof(ErrorSummary));

    /// <summary>True when the script has at least one syntax error — for binding an error strip's visibility.</summary>
    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<VariableAwareTextEditor, bool>(nameof(HasError));

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IReadOnlyDictionary<string, string>? Variables
    {
        get => GetValue(VariablesProperty);
        set => SetValue(VariablesProperty, value);
    }

    public bool SingleLine
    {
        get => GetValue(SingleLineProperty);
        set => SetValue(SingleLineProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public string? SyntaxHighlightingName
    {
        get => GetValue(SyntaxHighlightingNameProperty);
        set => SetValue(SyntaxHighlightingNameProperty, value);
    }

    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    public bool Bordered
    {
        get => GetValue(BorderedProperty);
        set => SetValue(BorderedProperty, value);
    }

    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public bool IsPassword
    {
        get => GetValue(IsPasswordProperty);
        set => SetValue(IsPasswordProperty, value);
    }

    public bool IsRevealed
    {
        get => GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    public string? WarningTip
    {
        get => GetValue(WarningTipProperty);
        set => SetValue(WarningTipProperty, value);
    }

    public EditorCompletionMode CompletionMode
    {
        get => GetValue(CompletionModeProperty);
        set => SetValue(CompletionModeProperty, value);
    }

    public CoreScriptKind ScriptKind
    {
        get => GetValue(ScriptKindProperty);
        set => SetValue(ScriptKindProperty, value);
    }

    public string? ErrorSummary
    {
        get => GetValue(ErrorSummaryProperty);
        set => SetValue(ErrorSummaryProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    private TextEditor _editor = null!;
    private CompletionWindow? _completionWindow;
    private bool _suppressTextSync;
    private TextBlock? _watermarkBlock;
    private ToggleButton? _eyeToggle;
    private Border? _warningGlyph;
    private StackPanel? _trailingGroup;
    private SquiggleRenderer? _squiggleRenderer;
    private DispatcherTimer? _diagnosticsTimer;
    private int _diagnosticsToken;
    private IReadOnlyList<ScriptDiagnostic> _diagnostics = Array.Empty<ScriptDiagnostic>();

    /// <summary>Current syntax diagnostics (empty when clean / not in script mode).</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Raised on the UI thread after the diagnostics set changes — hosts bind an error strip.</summary>
    public event EventHandler? DiagnosticsChanged;

    public VariableAwareTextEditor()
    {
        InitializeComponent();
        _editor = this.FindControl<TextEditor>("Editor")!;

        // Keep the inner TextEditor's font size in lockstep with the UserControl so callers can
        // bind FontSize="{DynamicResource EditorFontSize}" on the host for the body editors,
        // while URL/header/KV fields stay at the UserControl default (12).
        _editor.FontSize = FontSize;

        // Disable the auto-hyperlinks. AvaloniaEdit's default behavior renders any URL inside
        // the body (e.g. xmlns="http://…") as a clickable blue-underlined link, which is
        // noisy inside an XML / JSON editor and breaks the syntax-highlighting color scheme.
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;

        // Route text selection through the same translucent, themed brush the plain
        // TextBoxes use (Styles.axaml). AvaloniaEdit otherwise paints selection with a
        // saturated system-blue over forced-white text — illegible on light themes and
        // visibly inconsistent with the sibling KEY cell / URL-bar TextBoxes.
        ApplySelectionTheme();

        _editor.TextArea.TextView.LineTransformers.Add(new VariableColorizer(this));
        // Mask non-{{var}} runs with bullet glyphs when IsPassword=true && !IsRevealed.
        // {{var}} placeholders pass through unmasked so the user can identify which variable
        // is wired up (and hover to see its resolved value).
        _editor.TextArea.TextView.ElementGenerators.Add(new PasswordMaskGenerator(this));

        // Script-mode syntax squiggles. The renderer is always attached but only paints when
        // diagnostics are present (Script mode), so URL/KV fields are unaffected. Parsing is
        // debounced + offloaded; results are applied back on the UI thread.
        _squiggleRenderer = new SquiggleRenderer(UnresolvedBrush);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_squiggleRenderer);
        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _diagnosticsTimer.Tick += (_, _) => { _diagnosticsTimer!.Stop(); RunDiagnostics(); };

        // Find (Ctrl+F) match highlight: a translucent amber that stays legible on both light and
        // dark editor backgrounds (AvaloniaEdit's default rendered dark-on-dark in our theme).
        _editor.SearchResultsBrush = new SolidColorBrush(Color.Parse("#66FBBF24"));

        // Re-colorize whenever the active theme variant flips, so the static
        // VariableBrush / UnresolvedBrush picks up the new palette and the
        // currently-rendered {{tokens}} repaint instead of carrying over the
        // previous theme's color.
        if (global::Avalonia.Application.Current is { } app)
        {
            app.PropertyChanged += (_, e) =>
            {
                if (e.Property == global::Avalonia.Application.ActualThemeVariantProperty)
                    // The selection brush re-resolves itself (it's bound to the themed
                    // resource observable); just repaint so the {{var}} token colors, which
                    // are picked statically per render, flip to the new palette.
                    _editor.TextArea.TextView.Redraw();
            };
        }

        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.TextArea.TextView.PointerHover += OnPointerHover;
        _editor.TextArea.TextView.PointerHoverStopped += OnPointerHoverStopped;

        // Single-line mode: kill Enter and strip newlines from pasted text.
        _editor.AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: false);

        // Render watermark over the editor area when empty.
        var host = this.FindControl<Border>("HostBorder")!;
        _watermarkBlock = new TextBlock
        {
            FontFamily = _editor.FontFamily,
            FontSize = _editor.FontSize,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            IsHitTestVisible = false,
        };
        // Placeholder colour comes from the theme's faint-text token, not a hardcoded
        // translucent white — that white was invisible on every light variant (the URL,
        // body, and KV-cell watermarks vanished). GetResourceObservable re-emits on theme
        // switch so the placeholder tracks the active variant live.
        _watermarkBlock.Bind(TextBlock.ForegroundProperty,
            this.GetResourceObservable("Text3Brush"));
        // Inline eye + warning glyphs. Both are stacked together on the right edge so the
        // layout stays stable as IsPassword / WarningTip flip at runtime. The eye drives
        // IsRevealed two-way so the property and the toggle stay in sync.
        _eyeToggle = new ToggleButton
        {
            IsVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            MinWidth = 0,
            MinHeight = 0,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Icon
            {
                Kind = IconKind.Eye,
                Size = 14,
                Foreground = this.TryFindResource("Text2Brush", out var iconBrushObj) && iconBrushObj is IBrush iconBrush
                    ? iconBrush : Brushes.Gray,
            },
        };
        ToolTip.SetTip(_eyeToggle, "Show / hide");
        _eyeToggle.IsCheckedChanged += (_, _) =>
        {
            IsRevealed = _eyeToggle!.IsChecked == true;
        };
        _warningGlyph = new Border
        {
            IsVisible = false,
            Padding = new Thickness(4),
            Child = new Icon
            {
                Kind = IconKind.Warning,
                Size = 14,
                Foreground = this.TryFindResource("StatusWarnBrush", out var warnBrushObj) && warnBrushObj is IBrush warnBrush
                    ? warnBrush : Brushes.Goldenrod,
            },
        };
        _trailingGroup = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible = true,
        };
        _trailingGroup.Children.Add(_eyeToggle);
        _trailingGroup.Children.Add(_warningGlyph);
        var grid = new Grid();
        var oldChild = host.Child!;
        host.Child = grid;
        grid.Children.Add(oldChild);
        grid.Children.Add(_watermarkBlock);
        grid.Children.Add(_trailingGroup);
        UpdateWatermark();
        UpdatePasswordChrome();

        // Apply line-mode preferences NOW so callers that default-construct the editor
        // with SingleLine=true (URL / KV fields) and callers that set SingleLine="False"
        // in XAML before the property change pump runs both end up with the right state.
        // Without this, multi-line bodies were starting with the XAML hardcoded
        // WordWrap="False" because ApplyMultiLineMode only ran on SingleLine *changes*.
        ApplyMultiLineMode();
        // Same reasoning for bordered/padding state — KV rows construct with the
        // default Bordered=True then flip to False from XAML, and we need to honor that
        // when SingleLine is also true (inline-cell shrink-to-text + center).
        ApplyBordered();

        // Defense in depth — AvaloniaEdit's TextEditor caches its wrap layout decision
        // at render time, and depending on the order in which XAML attributes vs. bindings
        // resolve, the editor can lock in WordWrap=false before our SingleLine setter
        // reaches it. Re-apply at every lifecycle moment we have access to:
        //
        //   1. AttachedToVisualTree — fires when added to a window (bindings may still be unresolved)
        //   2. Loaded — fires after the visual tree is fully realized and templates expanded
        //   3. Dispatcher post on Loaded — runs after the current layout pass, so any
        //      pending wrap recalculation happens against the final WordWrap value
        //
        // Triple-belt-and-suspenders. AvaloniaEdit is finicky here.
        AttachedToVisualTree += (_, _) => ApplyMultiLineMode();
        _editor.Loaded += (_, _) =>
        {
            ApplyMultiLineMode();
            Dispatcher.UIThread.Post(ApplyMultiLineMode);
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            var incoming = change.GetNewValue<string>() ?? string.Empty;
            if (_editor.Document.Text != incoming)
            {
                _suppressTextSync = true;
                try { _editor.Document.Text = incoming; }
                finally { _suppressTextSync = false; }
            }
            UpdateWatermark();
            // Text set via binding suppresses the editor's TextChanged, so re-analyze here so a
            // script loaded with pre-existing errors squiggles immediately, not only after typing.
            if (CompletionMode == EditorCompletionMode.Script) ScheduleDiagnostics();
        }
        else if (change.Property == CompletionModeProperty)
        {
            if (CompletionMode == EditorCompletionMode.Script) ScheduleDiagnostics();
        }
        else if (change.Property == VariablesProperty)
        {
            // Re-render to refresh resolved-vs-unresolved coloring.
            _editor.TextArea.TextView.Redraw();
        }
        else if (change.Property == WatermarkProperty)
        {
            if (_watermarkBlock is not null) _watermarkBlock.Text = Watermark;
        }
        else if (change.Property == SyntaxHighlightingNameProperty)
        {
            ApplySyntaxHighlighting();
        }
        else if (change.Property == ShowLineNumbersProperty)
        {
            _editor.ShowLineNumbers = ShowLineNumbers;
        }
        else if (change.Property == SingleLineProperty)
        {
            ApplyMultiLineMode();
            // SingleLine controls inline-cell vs full-pane sizing in ApplyBordered too —
            // re-run so a SingleLine flip after construction updates the host's vertical
            // alignment + horizontal padding.
            ApplyBordered();
        }
        else if (change.Property == WordWrapProperty)
        {
            // Re-apply layout when the host flips wrap.
            ApplyMultiLineMode();
        }
        else if (change.Property == BorderedProperty)
        {
            ApplyBordered();
        }
        else if (change.Property == FontSizeProperty)
        {
            // Forward to the inner editor + watermark so the live setting (EditorFontSize)
            // takes effect immediately when the user changes Settings → Font size.
            var size = change.GetNewValue<double>();
            if (_editor is not null) _editor.FontSize = size;
            if (_watermarkBlock is not null) _watermarkBlock.FontSize = size;
        }
        else if (change.Property == IsPasswordProperty || change.Property == IsRevealedProperty)
        {
            UpdatePasswordChrome();
            // The mask generator decides per-line whether to bullet a run; force a redraw so
            // the new IsPassword / IsRevealed state actually paints to screen.
            _editor.TextArea.TextView.Redraw();
        }
        else if (change.Property == WarningTipProperty)
        {
            UpdatePasswordChrome();
        }
    }

    private void UpdatePasswordChrome()
    {
        if (_eyeToggle is null || _warningGlyph is null) return;
        _eyeToggle.IsVisible = IsPassword;
        if (_eyeToggle.IsChecked != IsRevealed)
            _eyeToggle.IsChecked = IsRevealed;

        var warn = WarningTip;
        var showWarn = !string.IsNullOrEmpty(warn);
        _warningGlyph.IsVisible = showWarn;
        if (showWarn) ToolTip.SetTip(_warningGlyph, warn);

        // Reserve room on the right side of the input so the editor's trailing text doesn't
        // slide under the inline glyphs. One glyph ≈ 22px; two glyphs ≈ 44px.
        var reserved = (IsPassword ? 22 : 0) + (showWarn ? 22 : 0);
        if (this.FindControl<Border>("HostBorder") is { } host)
        {
            var basePad = host.Padding;
            host.Padding = reserved > 0
                ? new Thickness(basePad.Left, basePad.Top, Math.Max(basePad.Right, reserved), basePad.Bottom)
                : new Thickness(basePad.Left, basePad.Top, basePad.Right >= 22 ? 6 : basePad.Right, basePad.Bottom);
        }
    }

    /// <summary>Points the inner AvaloniaEdit selection at the shared themed
    /// <c>SelectionBrush</c> (a translucent accent the plain TextBoxes also use) and clears
    /// SelectionForeground so selected text keeps its own foreground — including the
    /// <c>{{var}}</c> amber/red token colors — instead of AvaloniaEdit's default
    /// opaque-blue-with-forced-white, which read as illegible dark-blue blocks.</summary>
    private void ApplySelectionTheme()
    {
        if (_editor is null) return;
        // Bind (not TryFindResource) the selection brush: this method runs from the
        // constructor before the control is attached, so a one-shot lookup resolves against
        // the wrong/empty theme scope and silently misses — leaving AvaloniaEdit's default
        // opaque blue. GetResourceObservable resolves lazily against the live theme variant
        // and re-emits on theme switch, and binds at LocalValue priority so it wins over
        // AvaloniaEdit's own default. SelectionForeground stays null so selected text keeps
        // its own foreground (including {{var}} amber/red) instead of forced white.
        _editor.TextArea.Bind(
            TextArea.SelectionBrushProperty,
            this.GetResourceObservable("SelectionBrush"));
        _editor.TextArea.SelectionForeground = null;
    }

    private void ApplyBordered()
    {
        var host = this.FindControl<global::Avalonia.Controls.Border>("HostBorder");
        if (host is null) return;
        if (Bordered)
        {
            host.BorderThickness = new Thickness(1);
            // SingleLine cells (KV Value column) use the same 6,3 padding as the sibling
            // TextBox so the two cells line up pixel-for-pixel. Multi-line editors keep
            // the slightly taller 6,5 — they have line-numbers/wrap chrome below and an
            // extra row of vertical breathing room reads better there.
            host.Padding = SingleLine ? new Thickness(6, 3) : new Thickness(6, 5);
            host.CornerRadius = new global::Avalonia.CornerRadius(3);
            host.Background = this.TryFindResource("BgInputBrush", out var bg) && bg is IBrush bgBrush
                ? bgBrush : Brushes.Transparent;
            host.BorderBrush = this.TryFindResource("Border1Brush", out var bb) && bb is IBrush bbBrush
                ? bbBrush : Brushes.Transparent;
            host.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
        }
        else
        {
            host.BorderThickness = new Thickness(0);
            host.CornerRadius = new global::Avalonia.CornerRadius(0);
            host.Background = Brushes.Transparent;
            host.Padding = new Thickness(0);
            host.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
        }
        // Padding may need to grow to reserve room for the eye toggle — re-apply here so
        // a Bordered/SingleLine change after construction picks up the password chrome too.
        UpdatePasswordChrome();
    }

    private void ApplySyntaxHighlighting()
    {
        var name = SyntaxHighlightingName;
        if (string.IsNullOrWhiteSpace(name))
        {
            _editor.SyntaxHighlighting = null;
            return;
        }
        try
        {
            // HighlightingManager.Instance.GetDefinition is case-insensitive and tolerant of
            // unknown names (returns null) — leave the editor unhighlighted on miss.
            var def = HighlightingManager.Instance.GetDefinition(name);
            // Patch named colors BEFORE handing the definition to the editor —
            // the colorizer captures color references at assignment time, so
            // mutations afterward only take effect on the next redraw. Patched
            // definitions are application-global, so every editor using the
            // same name picks up the override.
            EditorSyntaxTheme.Apply(def);
            _editor.SyntaxHighlighting = def;
            // Belt-and-braces: invalidate the text view so already-rendered
            // lines re-colorize when the user toggles theme.
            _editor.TextArea.TextView.Redraw();
        }
        catch
        {
            _editor.SyntaxHighlighting = null;
        }
    }

    private void ApplyMultiLineMode()
    {
        var multi = !SingleLine;
        // Single-line URL fields force wrap off so URLs scroll horizontally instead of
        // wrapping mid-token. Multi-line editors take their wrap state from the per-instance
        // <see cref="WordWrap"/> property (default true) — the previous global
        // <c>EditorWordWrap</c> app resource couldn't be overridden per-editor and made
        // long body lines stay un-wrapped when the user toggled it off elsewhere.
        var wordWrap = multi && WordWrap;
        _editor.WordWrap = wordWrap;
        // Single-line fields (URL bar / KV value cell) center their one line within the host
        // border so they line up with the sibling plain TextBoxes (KEY cell, GET dropdown),
        // which center via VerticalContentAlignment. Letting the editor Stretch makes
        // AvaloniaEdit top-align the text, so it floated above the centered neighbors.
        // Multi-line editors Stretch so the document fills the pane and scrolls normally.
        _editor.VerticalAlignment = multi
            ? global::Avalonia.Layout.VerticalAlignment.Stretch
            : global::Avalonia.Layout.VerticalAlignment.Center;
        _editor.HorizontalScrollBarVisibility = multi
            ? global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : global::Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        _editor.VerticalScrollBarVisibility = multi
            ? global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : global::Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;

        if (multi)
        {
            // Push the global tab size and line-numbers preference. The caller can still
            // force ShowLineNumbers=true explicitly via the StyledProperty when needed.
            var tabSize = ReadResourceInt("EditorTabSize", 2);
            _editor.Options.IndentationSize = tabSize;
            // Convert-tabs-to-spaces stays on for the body editors; AvaloniaEdit's default.
            _editor.Options.ConvertTabsToSpaces = true;

            // Only auto-flip ShowLineNumbers when the caller hasn't set it explicitly to
            // true (script editors do that). For URL/header fields SingleLine is true so
            // this branch never runs.
            if (!ShowLineNumbers)
            {
                var globalShow = ReadResourceBool("EditorShowLineNumbers", true);
                _editor.ShowLineNumbers = globalShow;
            }
        }
    }

    private static bool ReadResourceBool(string key, bool fallback)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return fallback;
        if (app.Resources.TryGetResource(key, null, out var v) && v is bool b) return b;
        return fallback;
    }

    private static int ReadResourceInt(string key, int fallback)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return fallback;
        if (app.Resources.TryGetResource(key, null, out var v))
        {
            if (v is int i) return i;
            if (v is double d) return (int)d;
        }
        return fallback;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextSync) return;
        var t = _editor.Document.Text;
        if (Text != t) Text = t;
        UpdateWatermark();
        if (CompletionMode == EditorCompletionMode.Script) ScheduleDiagnostics();
    }

    private void UpdateWatermark()
    {
        if (_watermarkBlock is null) return;
        _watermarkBlock.IsVisible = string.IsNullOrEmpty(_editor.Document.Text);
        _watermarkBlock.Text = Watermark;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Space → script autocomplete (member list if after `obj.`, else top-level objects).
        if (CompletionMode == EditorCompletionMode.Script
            && e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ShowScriptCompletion();
            e.Handled = true;
            return;
        }

        if (SingleLine && e.Key == Key.Enter)
        {
            e.Handled = true;
        }
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (SingleLine && e.Text is { } t && (t.Contains('\n') || t.Contains('\r')))
        {
            e.Text = t.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        // Variables mode: open the completion window after the second '{' of "{{".
        if (CompletionMode == EditorCompletionMode.Variables && e.Text == "{")
        {
            var caret = _editor.CaretOffset;
            if (caret >= 2 && _editor.Document.GetCharAt(caret - 2) == '{')
            {
                ShowCompletion();
            }
            return;
        }

        // Script mode: open member completion after a '.'.
        if (CompletionMode == EditorCompletionMode.Script && e.Text == ".")
        {
            ShowScriptCompletion();
        }
    }

    private void ShowCompletion()
    {
        var vars = Variables;
        if (vars is null || vars.Count == 0) return;
        if (_completionWindow is not null) return;

        var window = new CompletionWindow(_editor.TextArea);
        var data = window.CompletionList.CompletionData;
        foreach (var name in vars.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            data.Add(new VariableCompletionData(name, vars[name]));
        }

        window.Closed += (_, _) => _completionWindow = null;
        _completionWindow = window;
        window.Show();
    }

    // ---- Script autocomplete + diagnostics ----

    /// <summary>Opens JS autocomplete: member list when the caret follows <c>obj.</c>, otherwise
    /// the top-level object list for the current <see cref="ScriptKind"/>.</summary>
    private void ShowScriptCompletion()
    {
        if (CompletionMode != EditorCompletionMode.Script) return;

        var doc = _editor.Document;
        var caret = _editor.CaretOffset;

        // Extent of the identifier currently being typed (the filter prefix / replace segment).
        var wordStart = caret;
        while (wordStart > 0 && IsIdentChar(doc.GetCharAt(wordStart - 1))) wordStart--;

        IEnumerable<ICompletionData> items;
        if (wordStart > 0 && doc.GetCharAt(wordStart - 1) == '.')
        {
            var expr = ExtractAccessExpression(doc, wordStart - 1);
            var obj = ScriptApiCatalog.Resolve(expr, ScriptKind);
            if (obj is null || obj.Members.Count == 0) return;
            items = obj.Members.Select(m => (ICompletionData)new ScriptMemberCompletionData(m));
        }
        else
        {
            items = ScriptApiCatalog.TopLevel(ScriptKind).Select(o => (ICompletionData)new ScriptObjectCompletionData(o));
        }

        OpenCompletionWindow(items, wordStart, caret);
    }

    private void OpenCompletionWindow(IEnumerable<ICompletionData> items, int startOffset, int caret)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        // AvaloniaEdit reuses a single window; close any open one so a member popup can replace an
        // object popup (and vice-versa) without the stale-window guard blocking it.
        _completionWindow?.Close();

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = startOffset,
            EndOffset = caret,
        };
        foreach (var d in list) window.CompletionList.CompletionData.Add(d);
        window.Closed += (_, _) => _completionWindow = null;
        _completionWindow = window;
        window.Show();
    }

    /// <summary>Walks backwards from the dot at <paramref name="dotOffset"/> to capture the full
    /// member-access expression (identifiers, dots, and balanced call parens) so chains like
    /// <c>expect(res.getStatus())</c> or <c>bru.runner</c> resolve correctly.</summary>
    private static string ExtractAccessExpression(TextDocument doc, int dotOffset)
    {
        var i = dotOffset - 1;
        var depth = 0;
        while (i >= 0)
        {
            var c = doc.GetCharAt(i);
            if (c is ')' or ']') { depth++; i--; continue; }
            if (c is '(' or '[')
            {
                if (depth == 0) break;
                depth--; i--; continue;
            }
            if (depth > 0) { i--; continue; }       // inside parens — consume everything
            if (IsIdentChar(c) || c == '.') { i--; continue; }
            break;
        }
        var start = i + 1;
        return doc.GetText(start, dotOffset - start).Trim();
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>Restarts the debounce timer that drives background syntax analysis.</summary>
    private void ScheduleDiagnostics()
    {
        _diagnosticsTimer?.Stop();
        _diagnosticsTimer?.Start();
    }

    /// <summary>Parses the current text off the UI thread and applies the resulting squiggles back
    /// on the UI thread, dropping results superseded by a newer edit.</summary>
    private void RunDiagnostics()
    {
        if (CompletionMode != EditorCompletionMode.Script) return;
        var text = _editor.Document.Text;
        var token = ++_diagnosticsToken;
        System.Threading.Tasks.Task.Run(() =>
        {
            var diags = ScriptDiagnostics.Analyze(text);
            Dispatcher.UIThread.Post(() =>
            {
                if (token != _diagnosticsToken) return;
                _diagnostics = diags;
                _squiggleRenderer?.SetDiagnostics(diags, _editor.TextArea.TextView);
                if (diags.Count > 0)
                {
                    var d = diags[0];
                    ErrorSummary = $"Line {d.Line}, col {d.Column + 1}: {d.Message}";
                    HasError = true;
                }
                else
                {
                    ErrorSummary = null;
                    HasError = false;
                }
                DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    private void OnPointerHover(object? sender, PointerEventArgs e)
    {
        var pos = _editor.TextArea.TextView.GetPosition(e.GetPosition(_editor.TextArea.TextView) + _editor.TextArea.TextView.ScrollOffset);
        if (pos is null) return;

        var line = pos.Value.Line;
        if (line < 1 || line > _editor.Document.LineCount) return;

        var docLine = _editor.Document.GetLineByNumber(line);
        var lineText = _editor.Document.GetText(docLine.Offset, docLine.Length);
        var col = pos.Value.Column - 1;
        if (col < 0) return;

        // Script mode: show the syntax-error message when hovering over a squiggle.
        if (CompletionMode == EditorCompletionMode.Script && _diagnostics.Count > 0)
        {
            var hoverOffset = docLine.Offset + col;
            foreach (var d in _diagnostics)
            {
                if (hoverOffset >= d.Offset && hoverOffset <= d.Offset + d.Length)
                {
                    ToolTip.SetTip(_editor, d.Message);
                    ToolTip.SetIsOpen(_editor, true);
                    return;
                }
            }
        }

        // Find {{...}} containing the column.
        foreach (Match m in VarRegex.Matches(lineText))
        {
            if (col >= m.Index && col <= m.Index + m.Length)
            {
                var name = m.Groups[1].Value.Trim();
                var resolved = ResolveValue(name);
                ShowHoverTip(e, name, resolved);
                return;
            }
        }
        HideHoverTip();
    }

    private void OnPointerHoverStopped(object? sender, PointerEventArgs e) => HideHoverTip();

    private string? ResolveValue(string name)
    {
        var vars = Variables;
        if (vars is null) return null;
        return vars.TryGetValue(name, out var v) ? v : null;
    }

    private void ShowHoverTip(PointerEventArgs e, string name, string? resolved)
    {
        // Just the value when resolved (the {{name}} is already on screen — repeating it
        // in the tooltip is noise). When unset, show the name + "(unset)" so the user
        // still knows which placeholder is broken.
        var content = resolved is null ? $"{name}  (unset)" : resolved;
        ToolTip.SetTip(_editor, content);
        ToolTip.SetIsOpen(_editor, true);
    }

    private void HideHoverTip()
    {
        ToolTip.SetIsOpen(_editor, false);
    }

    /// <summary>Colorizes <c>{{name}}</c> tokens. Resolved → amber; unset → red.</summary>
    private sealed class VariableColorizer : DocumentColorizingTransformer
    {
        private readonly VariableAwareTextEditor _owner;

        public VariableColorizer(VariableAwareTextEditor owner) => _owner = owner;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            var text = CurrentContext.Document.GetText(line.Offset, line.Length);
            var vars = _owner.Variables;

            foreach (Match m in VarRegex.Matches(text))
            {
                var name = m.Groups[1].Value.Trim();
                var brush = vars is not null && vars.ContainsKey(name) ? VariableBrush : UnresolvedBrush;
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, e =>
                {
                    e.TextRunProperties.SetForegroundBrush(brush);
                });
            }
        }
    }

    /// <summary>Single-row entry in the autocomplete popup. <c>Text</c> is what gets inserted —
    /// we close the <c>}}</c> automatically since the user already typed <c>{{</c>.</summary>
    private sealed class VariableCompletionData : ICompletionData
    {
        private readonly string _name;
        private readonly string _value;

        public VariableCompletionData(string name, string value)
        {
            _name = name;
            _value = value;
        }

        public global::Avalonia.Media.IImage? Image => null;
        public string Text => _name;

        // Show only the variable name in the popup row. The resolved value is available
        // through Description (AvaloniaEdit shows it in the side detail panel) and via
        // the hover tooltip on the inserted {{var}} once typed.
        public object Content => BuildRow();
        public object Description => _value;
        public double Priority => 0;

        private Control BuildRow()
        {
            // Plain TextBlock with no explicit Foreground — inherits the popup's themed
            // text color so names render legibly on both light and dark themes (the
            // hardcoded #E5E7EB was invisible on light backgrounds).
            return new TextBlock
            {
                Text = _name,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            };
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            // Insert "name}}" — the leading "{{" is already in the document.
            textArea.Document.Replace(completionSegment, _name + "}}");
        }
    }

    /// <summary>Completion entry for a top-level script object (<c>bru</c>, <c>res</c>, …).</summary>
    private sealed class ScriptObjectCompletionData : ICompletionData
    {
        private readonly ScriptObject _obj;
        public ScriptObjectCompletionData(ScriptObject obj) => _obj = obj;

        public global::Avalonia.Media.IImage? Image => null;
        public string Text => _obj.Name;
        public object Content => MonoRow(_obj.Name, FontWeight.SemiBold);
        public object Description => _obj.Description;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, _obj.Name);
    }

    /// <summary>Completion entry for a member of a script object. Methods insert <c>name()</c> with
    /// the caret placed inside the parens when the method takes arguments.</summary>
    private sealed class ScriptMemberCompletionData : ICompletionData
    {
        private readonly ScriptMember _member;
        public ScriptMemberCompletionData(ScriptMember member) => _member = member;

        public global::Avalonia.Media.IImage? Image => null;
        public string Text => _member.Name;
        public object Content => MonoRow(_member.Signature, FontWeight.Normal);
        public object Description => _member.Description;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (_member.Kind == ScriptMemberKind.Method)
            {
                textArea.Document.Replace(completionSegment, _member.Name + "()");
                // Place the caret between the parens when the method takes args; otherwise after them.
                var hasParams = HasParams(_member.Signature);
                textArea.Caret.Offset = completionSegment.Offset + _member.Name.Length + (hasParams ? 1 : 2);
            }
            else
            {
                textArea.Document.Replace(completionSegment, _member.Name);
            }
        }

        private static bool HasParams(string signature)
        {
            var open = signature.IndexOf('(');
            return open >= 0 && open + 1 < signature.Length && signature[open + 1] != ')';
        }
    }

    /// <summary>Monospace popup row shared by the script completion entries.</summary>
    private static Control MonoRow(string text, FontWeight weight) => new TextBlock
    {
        Text = text,
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
        FontSize = 12,
        FontWeight = weight,
    };

    /// <summary>Emits <see cref="MaskedVisualLineText"/> elements over any run of characters
    /// that lies OUTSIDE a <c>{{name}}</c> placeholder when the editor is in password mode and
    /// not currently revealed. Variable tokens fall through to the default colorizer (amber /
    /// red) so the user can still tell at a glance which placeholder is wired up.</summary>
    private sealed class PasswordMaskGenerator : VisualLineElementGenerator
    {
        private readonly VariableAwareTextEditor _owner;
        public PasswordMaskGenerator(VariableAwareTextEditor owner) { _owner = owner; }

        private bool Active => _owner.IsPassword && !_owner.IsRevealed;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!Active) return -1;
            var ctx = CurrentContext;
            if (ctx is null) return -1;
            var firstLine = ctx.VisualLine.FirstDocumentLine;
            var lastLine = ctx.VisualLine.LastDocumentLine;
            var rangeStart = firstLine.Offset;
            var rangeEnd = lastLine.EndOffset;
            if (startOffset >= rangeEnd) return -1;

            var rangeText = ctx.Document.GetText(rangeStart, rangeEnd - rangeStart);
            var rel = Math.Max(0, startOffset - rangeStart);

            // Skip past any {{var}} the caret happens to start inside; pick the first
            // non-variable position at or after that.
            foreach (Match m in VarRegex.Matches(rangeText))
            {
                if (rel < m.Index) break;
                if (rel >= m.Index && rel < m.Index + m.Length)
                    rel = m.Index + m.Length;
            }
            return rel >= rangeText.Length ? -1 : rangeStart + rel;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (!Active) return null;
            var ctx = CurrentContext;
            if (ctx is null) return null;
            var visualLine = ctx.VisualLine;
            var firstLine = visualLine.FirstDocumentLine;
            var lastLine = visualLine.LastDocumentLine;
            var rangeStart = firstLine.Offset;
            var rangeEnd = lastLine.EndOffset;

            var rangeText = ctx.Document.GetText(rangeStart, rangeEnd - rangeStart);
            var rel = offset - rangeStart;
            if (rel < 0 || rel >= rangeText.Length) return null;

            // Run length: from rel to the next {{var}} start (or end of the line).
            var end = rangeText.Length;
            foreach (Match m in VarRegex.Matches(rangeText))
            {
                if (m.Index > rel && m.Index < end)
                {
                    end = m.Index;
                    break;
                }
            }
            var length = end - rel;
            if (length <= 0) return null;
            return new MaskedVisualLineText(visualLine, length);
        }
    }

    /// <summary>A drop-in <see cref="VisualLineText"/> that paints bullet glyphs instead of the
    /// underlying document characters. Used by <see cref="PasswordMaskGenerator"/> to render
    /// password-style masking without altering the document Text — bindings still see real
    /// characters; only what's painted on screen is replaced.</summary>
    private sealed class MaskedVisualLineText : VisualLineText
    {
        private const char Bullet = '•';

        public MaskedVisualLineText(VisualLine parent, int length) : base(parent, length) { }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            var relativeOffset = startVisualColumn - VisualColumn;
            var length = DocumentLength - relativeOffset;
            if (length <= 0) length = 1;
            return new TextCharacters(new string(Bullet, length).AsMemory(), TextRunProperties);
        }

        protected override VisualLineText CreateInstance(int length)
            => new MaskedVisualLineText(ParentVisualLine, length);
    }
}
