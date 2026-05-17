using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Vegha.App.Controls.Workspace;

/// <summary>
/// Reusable 4-column key/value row: [enabled checkbox][key][value][remove ✕]. Used by
/// the Params / Headers / Vars KvEditor tabs. Encapsulates the bordered-cell styling that
/// matches OAuth2 Additional Parameters and multipart/form-urlencoded body rows, so all
/// key/value tables across the app share one source of truth for row layout and chrome.
///
/// The Key cell automatically switches between plain TextBox and AutoCompleteBox depending
/// on whether <see cref="HeaderSuggestions"/> is set — Headers mode supplies the well-known
/// HTTP header names; Params / Vars leave it null and get a plain editor.
///
/// The Value cell is a <see cref="VariableAwareTextEditor"/> so <c>{{var}}</c> tokens
/// highlight and complete inside the cell — that's the whole reason Params/Headers/Vars
/// need their own row component rather than reusing the OAuth2 Additional Parameters
/// TextBox-only template.
/// </summary>
public partial class KvTableRow : UserControl
{
    /// <summary>Optional autocomplete source for the Key cell. When set (Headers mode),
    /// the row swaps the plain TextBox for an AutoCompleteBox bound to this list. Leave
    /// null for Params / Vars to keep the plain editor.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> HeaderSuggestionsProperty =
        AvaloniaProperty.Register<KvTableRow, IReadOnlyList<string>?>(nameof(HeaderSuggestions));

    /// <summary>Variable snapshot pushed to the value editor so <c>{{name}}</c> tokens
    /// resolve and hover-tooltips work. Bound from the parent workspace's
    /// <c>ResolvedVariablesSnapshot</c>.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> VariablesProperty =
        AvaloniaProperty.Register<KvTableRow, IReadOnlyDictionary<string, string>?>(nameof(Variables));

    /// <summary>Command invoked when the row's ✕ button is clicked. CommandParameter is
    /// bound to the row's DataContext (the KvEntry) so the parent collection can locate
    /// and remove the right item.</summary>
    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<KvTableRow, ICommand?>(nameof(RemoveCommand));

    /// <summary>Watermark text shown in the Key cell when empty. Defaults to "Key".</summary>
    public static readonly StyledProperty<string> KeyWatermarkProperty =
        AvaloniaProperty.Register<KvTableRow, string>(nameof(KeyWatermark), defaultValue: "Key");

    /// <summary>Watermark text shown in the Value cell when empty. Defaults to "Value".</summary>
    public static readonly StyledProperty<string> ValueWatermarkProperty =
        AvaloniaProperty.Register<KvTableRow, string>(nameof(ValueWatermark), defaultValue: "Value");

    public IReadOnlyList<string>? HeaderSuggestions
    {
        get => GetValue(HeaderSuggestionsProperty);
        set => SetValue(HeaderSuggestionsProperty, value);
    }

    public IReadOnlyDictionary<string, string>? Variables
    {
        get => GetValue(VariablesProperty);
        set => SetValue(VariablesProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public string KeyWatermark
    {
        get => GetValue(KeyWatermarkProperty);
        set => SetValue(KeyWatermarkProperty, value);
    }

    public string ValueWatermark
    {
        get => GetValue(ValueWatermarkProperty);
        set => SetValue(ValueWatermarkProperty, value);
    }

    public KvTableRow()
    {
        InitializeComponent();
    }
}
