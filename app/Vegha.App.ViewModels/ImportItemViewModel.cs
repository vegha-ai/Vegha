using CommunityToolkit.Mvvm.ComponentModel;
using Vegha.Core.Importers;

namespace Vegha.App.ViewModels;

/// <summary>Wraps a single <see cref="ImportResult"/> with selection state for the bulk
/// import list. Lives here (in the VM layer) rather than on <see cref="ImportResult"/>
/// itself so the importers project stays presentation-free.</summary>
public sealed partial class ImportItemViewModel : ObservableObject
{
    public ImportResult Result { get; }

    /// <summary>Display name shown in the bulk-import checkbox row. Prefers the parsed
    /// collection's <c>Name</c>; falls back to a short label for env-only or unrecognized
    /// rows so the list never has blank entries.</summary>
    public string DisplayName { get; }

    /// <summary>Format label propagated from the importer (e.g. "Bruno folder",
    /// "Postman v2.1"). Used for the secondary line in the row.</summary>
    public string FormatLabel => Result.FormatLabel;

    /// <summary>True when the row contributed a collection — drives whether the row counts
    /// toward the "Collections (N)" header in the wizard.</summary>
    public bool IsCollection => Result.Collection is not null;

    /// <summary>Per-row selection. Toggled by the row's checkbox; the wizard's Import command
    /// only invokes confirmed-callbacks for items where this is true.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    public ImportItemViewModel(ImportResult result)
    {
        Result = result;
        DisplayName = result.Collection?.Name
                      ?? result.Environment?.Name
                      ?? (string.IsNullOrEmpty(result.FormatLabel) ? "(unrecognized)" : result.FormatLabel);
    }
}
