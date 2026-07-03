using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vegha.App.ViewModels;

/// <summary>
/// Backs the Ctrl+E quick collection switcher — an Alt+Tab-style overlay listing the current
/// workspace's OPEN collections (MRU-ordered, active first). The overlay opens with the SECOND
/// row pre-selected so a quick hold-Ctrl / tap-E / release-Ctrl jumps to the previously-used
/// collection, exactly like the OS window switcher.
/// </summary>
public sealed partial class QuickSwitcherViewModel : ObservableObject
{
    private readonly CollectionsViewModel _collections;
    private readonly WorkspacesViewModel _workspaces;

    public ObservableCollection<QuickSwitcherRow> Rows { get; } = new();

    [ObservableProperty]
    private int _selectedIndex;

    public bool HasRows => Rows.Count > 0;

    public QuickSwitcherViewModel(CollectionsViewModel collections, WorkspacesViewModel workspaces)
    {
        _collections = collections;
        _workspaces = workspaces;
        BuildRows();
        // Start on the second row (the previously-used collection) so a tap-and-release of the
        // chord toggles between the two most-recent collections. Falls back to 0 when there's
        // only one row.
        SelectedIndex = Rows.Count > 1 ? 1 : 0;
    }

    private void BuildRows()
    {
        // The current workspace's OPEN set only (MRU order — active collection is first).
        var openPaths = _workspaces.ActiveWorkspace?.OpenCollectionPaths ?? new List<string>();
        foreach (var path in openPaths)
        {
            var root = _collections.AvailableCollections.FirstOrDefault(c =>
                string.Equals(c.SourcePath, path, StringComparison.OrdinalIgnoreCase));
            if (root is not null) Rows.Add(new QuickSwitcherRow(root.Name, root));
        }
    }

    /// <summary>Advances the selection by <paramref name="delta"/> (wraps). Used by the chord's
    /// repeated Ctrl+E taps and the arrow keys.</summary>
    public void Move(int delta)
    {
        if (Rows.Count == 0) return;
        SelectedIndex = ((SelectedIndex + delta) % Rows.Count + Rows.Count) % Rows.Count;
    }

    /// <summary>Switches the active collection to the selected row.</summary>
    public void Commit()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Rows.Count) return;
        _collections.ActiveCollection = Rows[SelectedIndex].Root;
    }
}

/// <summary>One row in the quick switcher — a collection in the current workspace's open set.</summary>
public sealed record QuickSwitcherRow(string Name, CollectionRootViewModel Root);
