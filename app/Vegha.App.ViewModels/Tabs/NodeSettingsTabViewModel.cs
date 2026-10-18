using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// Shared base for the collection- and folder-settings workspace tabs. Both edit through a
/// <see cref="NodePropertiesViewModel"/> and render with the same <c>NodeSettingsTab</c> view —
/// the collection variant adds environment counts and the Presets section, which
/// <see cref="IsCollection"/> gates.
/// </summary>
public abstract partial class NodeSettingsTabViewModel : RequestTabViewModel
{
    /// <summary>The editing surface. Reassigned on rehydrate after a save, so it raises
    /// PropertyChanged for the bound content to re-bind.</summary>
    [ObservableProperty]
    private NodePropertiesViewModel _props = null!;

    // ---- Overview stats ----
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private int _requestCount;

    /// <summary>Documentation edit/view toggle. The Overview shows EITHER the Markdown editor
    /// (when true) OR the rendered preview / empty-state placeholder (when false) — never both.
    /// Resets to view mode on (re)hydrate.</summary>
    [ObservableProperty] private bool _isDocsEditing;

    /// <summary>The on-disk directory this tab edits (collection root or folder) — stable
    /// across reloads, which swap the node VM instances.</summary>
    public abstract string NodePath { get; }

    public abstract bool IsCollection { get; }

    /// <summary>Header subtitle — "Collection settings" / "Folder settings".</summary>
    public string KindLabel => IsCollection ? "Collection settings" : "Folder settings";

    /// <summary>Overview "Requests" tile caption.</summary>
    public string RequestCountLabel => IsCollection ? " request(s) in collection" : " request(s) in folder";

    public override object Workspace => this;

    /// <summary>Flips between the docs editor and the rendered preview.</summary>
    [RelayCommand]
    private void ToggleDocsEdit() => IsDocsEditing = !IsDocsEditing;

    /// <summary>Swaps in a fresh editing surface, rewiring the save hook.</summary>
    protected void SetProps(NodePropertiesViewModel props)
    {
        if (Props is not null) Props.SaveRequested -= OnPropsSaved;
        props.SaveRequested += OnPropsSaved;
        Props = props;
        // Land in view mode after (re)hydrate — a fresh save returns you to the rendered docs.
        IsDocsEditing = false;
    }

    private void OnPropsSaved(object? sender, NodePropertiesSaveEventArgs e)
    {
        Save(e.Snapshot);
        IsDirty = false;
    }

    /// <summary>Writes the snapshot to disk and rehydrates from the reloaded node.</summary>
    protected abstract void Save(NodeSnapshot snapshot);
}
