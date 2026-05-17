using Vegha.Core.Persistence;

namespace Vegha.App.ViewModels.Settings;

/// <summary>Shortcuts page VM. Read-only viewer over a list provided by the host (the
/// App project owns the catalog so it can stay in sync with the actual KeyBinding /
/// MenuItem InputGesture entries in XAML).</summary>
public sealed class ShortcutsSettingsViewModel : SettingsPageBase
{
    public override string Id => "shortcuts";
    public override string Title => "Shortcuts";
    public override string IconKey => "Keyboard";

    public IReadOnlyList<ShortcutRow> Rows { get; }

    public ShortcutsSettingsViewModel(IEnumerable<ShortcutRow> rows)
    {
        Rows = rows.ToList();
    }

    public override void ReadFrom(AppSettings settings) { /* read-only */ }
    public override AppSettings WriteTo(AppSettings existing) => existing;
}

public sealed record ShortcutRow(string Category, string Action, string Gesture);
