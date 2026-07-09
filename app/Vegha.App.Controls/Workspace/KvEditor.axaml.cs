using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Vegha.App.ViewModels;
using Vegha.Core.Domain;

namespace Vegha.App.Controls.Workspace;

public partial class KvEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<KvEditor, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<KvEditor, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<KvEditor, ICommand?>(nameof(RemoveCommand));

    /// <summary>Variable snapshot pushed to each row's value editor so {{var}} tokens light up
    /// and hover tooltips show resolved values. Bound from the parent workspace's
    /// <c>ResolvedVariablesSnapshot</c>.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> VariablesProperty =
        AvaloniaProperty.Register<KvEditor, IReadOnlyDictionary<string, string>?>(nameof(Variables));

    /// <summary>True when the editor is in bulk-edit (text) mode rather than the row table.
    /// Wired to the "Bulk edit" toggle in the footer. Switching modes round-trips the data
    /// through <see cref="KvBulkEditParser"/>.</summary>
    public static readonly StyledProperty<bool> IsBulkModeProperty =
        AvaloniaProperty.Register<KvEditor, bool>(nameof(IsBulkMode), defaultValue: false);

    /// <summary>Raw text shown in bulk-edit mode. Two-way bound to the TextBox; serialized
    /// from <see cref="ItemsSource"/> when entering bulk mode and parsed back when leaving.</summary>
    public static readonly StyledProperty<string> BulkTextProperty =
        AvaloniaProperty.Register<KvEditor, string>(nameof(BulkText), defaultValue: string.Empty);

    /// <summary>Editor mode selector. "default" gives the plain key/value experience used
    /// for params and vars. "headers" enables autocomplete for the Name cell (well-known
    /// HTTP headers) and, where the entered name has a known value-set, for the Value cell
    /// too (Content-Type → MIME types, Cache-Control → directives, etc.).</summary>
    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<KvEditor, string>(nameof(Mode), defaultValue: "default");

    /// <summary>Exposed to the XAML template so the headers-only autocomplete bindings can
    /// surface the static header-name list.</summary>
    public IReadOnlyList<string> HeaderNameSuggestions => HttpHeaderCatalog.RequestHeaderNames;

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public IReadOnlyDictionary<string, string>? Variables
    {
        get => GetValue(VariablesProperty);
        set => SetValue(VariablesProperty, value);
    }

    public bool IsBulkMode
    {
        get => GetValue(IsBulkModeProperty);
        set => SetValue(IsBulkModeProperty, value);
    }

    public string BulkText
    {
        get => GetValue(BulkTextProperty);
        set => SetValue(BulkTextProperty, value);
    }

    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>True when <see cref="Mode"/>="headers" — enables autocomplete on the Name
    /// column. Exposed as a property so XAML can pick between two row templates via a
    /// simple binding instead of a converter.</summary>
    public bool IsHeadersMode => string.Equals(Mode, "headers", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Header-name suggestions handed to each <c>KvTableRow</c> — non-null in
    /// Headers mode, null otherwise. The shared row UserControl checks for null to decide
    /// between plain TextBox and AutoCompleteBox for the Key cell.</summary>
    public IReadOnlyList<string>? RowHeaderSuggestions =>
        IsHeadersMode ? HttpHeaderCatalog.RequestHeaderNames : null;

    /// <summary>Watermark text shown in the Key cell when empty. Headers mode hints at the
    /// well-known-headers autocomplete; other modes show a plain "Key".</summary>
    public string RowKeyWatermark =>
        IsHeadersMode ? "Header (e.g. Content-Type)" : "Key";

    public KvEditor()
    {
        InitializeComponent();
    }

    /// <summary>Round-trips data between the row collection and <see cref="BulkText"/>
    /// when the user toggles <see cref="IsBulkMode"/>. Entering bulk mode: serialize rows
    /// to text. Leaving bulk mode: parse text back into rows.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsBulkModeProperty)
        {
            var nowBulk = change.GetNewValue<bool>();
            if (nowBulk) BulkText = SerializeItems();
            else        ApplyBulkText();
        }
        else if (change.Property == ModeProperty)
        {
            // IsHeadersMode + RowHeaderSuggestions + RowKeyWatermark are derived from Mode.
            // Raise change notifications so XAML bindings on the new properties re-evaluate.
            RaisePropertyChanged(IsHeadersModeProperty, !IsHeadersMode, IsHeadersMode);
            RaisePropertyChanged(RowHeaderSuggestionsProperty, null, RowHeaderSuggestions);
            RaisePropertyChanged(RowKeyWatermarkProperty, string.Empty, RowKeyWatermark);
        }
    }

    /// <summary>Direct routed-event ID for the derived <see cref="IsHeadersMode"/>. We
    /// register it as a routed event proxy via <c>Avalonia.AvaloniaProperty.RegisterDirect</c>
    /// so binding consumers see the change when <see cref="Mode"/> flips. This is the
    /// standard Avalonia idiom for derived-property change notification.</summary>
    private static readonly DirectProperty<KvEditor, bool> IsHeadersModeProperty =
        AvaloniaProperty.RegisterDirect<KvEditor, bool>(
            nameof(IsHeadersMode),
            owner => owner.IsHeadersMode);

    /// <summary>Direct-property mirror of <see cref="RowHeaderSuggestions"/> so XAML
    /// bindings refresh when <see cref="Mode"/> flips.</summary>
    private static readonly DirectProperty<KvEditor, IReadOnlyList<string>?> RowHeaderSuggestionsProperty =
        AvaloniaProperty.RegisterDirect<KvEditor, IReadOnlyList<string>?>(
            nameof(RowHeaderSuggestions),
            owner => owner.RowHeaderSuggestions);

    /// <summary>Direct-property mirror of <see cref="RowKeyWatermark"/> so XAML
    /// bindings refresh when <see cref="Mode"/> flips.</summary>
    private static readonly DirectProperty<KvEditor, string> RowKeyWatermarkProperty =
        AvaloniaProperty.RegisterDirect<KvEditor, string>(
            nameof(RowKeyWatermark),
            owner => owner.RowKeyWatermark);

    /// <summary>Reads the current ItemsSource and produces the bulk-edit text. Tolerant of
    /// the source being null or non-KvEntry — rows of unknown shape are skipped.</summary>
    private string SerializeItems()
    {
        if (ItemsSource is not IEnumerable src) return string.Empty;
        var pairs = new List<KvPair>();
        foreach (var item in src)
        {
            if (item is KvEntry kv && !string.IsNullOrEmpty(kv.Name))
                pairs.Add(new KvPair(kv.Name, kv.Value ?? string.Empty, kv.Enabled));
        }
        return KvBulkEditParser.Format(pairs);
    }

    /// <summary>Parses <see cref="BulkText"/> and replaces the underlying KvEntry rows.
    /// Requires the source to be an <c>IList&lt;KvEntry&gt;</c> (the typical
    /// ObservableCollection&lt;KvEntry&gt; from the ViewModel). When it isn't,
    /// the parsed result is discarded so the user's table data isn't lost silently.</summary>
    private void ApplyBulkText()
    {
        if (ItemsSource is not IList<KvEntry> list) return;
        var parsed = KvBulkEditParser.Parse(BulkText);

        list.Clear();
        foreach (var p in parsed)
            list.Add(new KvEntry(p.Name, p.Value, p.Enabled));
        // Restore the trailing ghost row the Clear() wiped. The VM-side auto-append
        // (KvAutoAppend) deliberately sits out Reset storms like this one, so the
        // repopulating code owns the invariant here.
        list.Add(new KvEntry());
    }
}
