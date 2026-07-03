using CommunityToolkit.Mvvm.ComponentModel;

namespace Vegha.App.ViewModels;

/// <summary>One row in a key-value editor (Params, Headers, etc.).</summary>
public partial class KvEntry : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _value = string.Empty;

    public KvEntry() { }
    public KvEntry(string name, string value, bool enabled = true)
    {
        _name = name;
        _value = value;
        _enabled = enabled;
    }

    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Name);

    /// <summary>True when both cells are empty — the auto-appended placeholder ("ghost") row
    /// at the tail of each KV table. Ghost rows hide their checkbox / remove button and are
    /// filtered out of every save path.</summary>
    public bool IsBlank => string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(Value);
}
