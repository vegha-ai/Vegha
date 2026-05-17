using CommunityToolkit.Mvvm.ComponentModel;

namespace Vegha.App.ViewModels;

/// <summary>One row in a key-value editor (Params, Headers, etc.).</summary>
public partial class KvEntry : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _value = string.Empty;

    public KvEntry() { }
    public KvEntry(string name, string value, bool enabled = true)
    {
        _name = name;
        _value = value;
        _enabled = enabled;
    }

    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Name);
}
