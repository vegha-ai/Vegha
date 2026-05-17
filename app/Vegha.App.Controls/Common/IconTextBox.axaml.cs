using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Vegha.App.Controls.Common;

/// <summary>
/// TextBox with right-side icon adorners docked INSIDE the input border. Built
/// for the "password with show/hide eye + plaintext-warning" pattern used by
/// the OAuth2 client secret field, but the adorners are independently
/// toggleable so the same control works wherever a single TextBox needs an
/// inline eye and/or warning glyph. Backed by Avalonia's
/// <c>TextBox.InnerRightContent</c> slot, so the icons render inside the
/// border instead of as separate sibling controls.
/// </summary>
public partial class IconTextBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconTextBox, string?>(
            nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<IconTextBox, string?>(nameof(Watermark));

    /// <summary>When true, an eye toggle is rendered on the right and clicking
    /// it flips <see cref="IsRevealed"/>. When <c>IsRevealed=false</c> the
    /// TextBox masks the value with the bullet glyph; when true it shows
    /// plaintext. Defaults to <c>false</c> so the control behaves like a plain
    /// labeled TextBox unless reveal is explicitly enabled.</summary>
    public static readonly StyledProperty<bool> ShowRevealProperty =
        AvaloniaProperty.Register<IconTextBox, bool>(nameof(ShowReveal));

    public static readonly StyledProperty<bool> IsRevealedProperty =
        AvaloniaProperty.Register<IconTextBox, bool>(
            nameof(IsRevealed), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Shows a warning glyph (passive — no command). Use the
    /// accompanying <see cref="WarningTip"/> to explain the advisory on hover
    /// (e.g. "stored as plaintext on disk").</summary>
    public static readonly StyledProperty<bool> ShowWarningProperty =
        AvaloniaProperty.Register<IconTextBox, bool>(nameof(ShowWarning));

    public static readonly StyledProperty<string?> WarningTipProperty =
        AvaloniaProperty.Register<IconTextBox, string?>(nameof(WarningTip));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public bool ShowReveal
    {
        get => GetValue(ShowRevealProperty);
        set => SetValue(ShowRevealProperty, value);
    }

    public bool IsRevealed
    {
        get => GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    public bool ShowWarning
    {
        get => GetValue(ShowWarningProperty);
        set => SetValue(ShowWarningProperty, value);
    }

    public string? WarningTip
    {
        get => GetValue(WarningTipProperty);
        set => SetValue(WarningTipProperty, value);
    }

    public IconTextBox()
    {
        InitializeComponent();
    }
}
