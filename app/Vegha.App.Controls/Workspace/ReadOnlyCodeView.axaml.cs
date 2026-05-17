using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Read-only AvaloniaEdit surface used for the response body and similar viewers.
/// Two-way bindable Text + a syntax-highlighter name (e.g. "Json", "XML") that flips
/// the colorizer live when the user changes the response format dropdown.
/// </summary>
public partial class ReadOnlyCodeView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ReadOnlyCodeView, string?>(
            nameof(Text), defaultValue: string.Empty);

    public static readonly StyledProperty<string?> SyntaxHighlightingNameProperty =
        AvaloniaProperty.Register<ReadOnlyCodeView, string?>(nameof(SyntaxHighlightingName));

    /// <summary>
    /// Controls whether the editor soft-wraps long lines. Defaults to <c>true</c> — most
    /// response bodies (JSON, XML, text/html) read better wrapped, and wrap avoids the
    /// scrollbar-drift issue where AvaloniaEdit's V-scrollbar can render at the right edge
    /// of the rendered text instead of the viewport when horizontal scrolling is enabled.
    /// Set to <c>false</c> when the caller specifically wants to inspect lines verbatim.
    /// </summary>
    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<ReadOnlyCodeView, bool>(nameof(WordWrap), defaultValue: true);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? SyntaxHighlightingName
    {
        get => GetValue(SyntaxHighlightingNameProperty);
        set => SetValue(SyntaxHighlightingNameProperty, value);
    }

    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    private TextEditor _editor = null!;

    public ReadOnlyCodeView()
    {
        InitializeComponent();
        _editor = this.FindControl<TextEditor>("Editor")!;
        // Push the initial WordWrap value imperatively — relying on the XAML binding alone
        // proved unreliable (AvaloniaEdit didn't always pick up the inherited value on
        // first layout, so long single-line responses still showed horizontally even
        // though the property read back as `true`).
        _editor.WordWrap = WordWrap;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            var incoming = change.GetNewValue<string?>() ?? string.Empty;
            if (_editor.Document.Text != incoming)
                _editor.Document.Text = incoming;
        }
        else if (change.Property == SyntaxHighlightingNameProperty)
        {
            var name = SyntaxHighlightingName;
            if (string.IsNullOrWhiteSpace(name))
            {
                _editor.SyntaxHighlighting = null;
                return;
            }
            try
            {
                var def = HighlightingManager.Instance.GetDefinition(name);
                EditorSyntaxTheme.Apply(def);
                _editor.SyntaxHighlighting = def;
                _editor.TextArea.TextView.Redraw();
            }
            catch { _editor.SyntaxHighlighting = null; }
        }
        else if (change.Property == WordWrapProperty)
        {
            // Push to the AvaloniaEdit editor whenever the host's WordWrap changes.
            _editor.WordWrap = change.GetNewValue<bool>();
        }
    }
}
