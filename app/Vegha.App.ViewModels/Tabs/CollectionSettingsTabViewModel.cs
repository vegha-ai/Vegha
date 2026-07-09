using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vegha.Core.Domain;

namespace Vegha.App.ViewModels.Tabs;

/// <summary>
/// Workspace tab hosting a collection's settings — Bruno-style Overview (location, request +
/// environment counts, docs) plus the Headers / Vars / Auth / Script / Tests / Presets
/// editors. Opening it as a tab (rather than a dialog) keeps the design ethos intact: the
/// main window only ever shows the selected collection's own info.
///
/// The editing surface is a <see cref="NodePropertiesViewModel"/> (collection kind), the same
/// model the folder Properties dialog uses — so the two stay in lockstep. Save re-resolves the
/// root by path through <see cref="CollectionsViewModel.ApplyCollectionSettings"/> and
/// rehydrates from the reloaded state.
/// </summary>
public sealed partial class CollectionSettingsTabViewModel : RequestTabViewModel
{
    private readonly CollectionsViewModel _collections;

    /// <summary>The collection root's on-disk path — stable across reloads (which swap the
    /// root VM instance), so we key + re-resolve by it rather than a captured reference.</summary>
    public string CollectionSourcePath { get; }

    /// <summary>The editing surface. Reassigned on rehydrate after a save, so it raises
    /// PropertyChanged for the bound content to re-bind.</summary>
    [ObservableProperty]
    private NodePropertiesViewModel _props = null!;

    // ---- Overview stats ----
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private int _requestCount;
    [ObservableProperty] private int _collectionEnvCount;
    [ObservableProperty] private int _globalEnvCount;

    /// <summary>Documentation edit/view toggle. The Overview shows EITHER the Markdown editor
    /// (when true) OR the rendered preview / empty-state placeholder (when false) — never both.
    /// Resets to view mode on (re)hydrate.</summary>
    [ObservableProperty] private bool _isDocsEditing;

    /// <summary>Flips between the docs editor and the rendered preview.</summary>
    [RelayCommand]
    private void ToggleDocsEdit() => IsDocsEditing = !IsDocsEditing;

    public override object Workspace => this;

    public static string BuildId(string sourcePath) => "colsettings:" + sourcePath;

    public CollectionSettingsTabViewModel(CollectionsViewModel collections, CollectionRootViewModel root)
    {
        _collections = collections;
        CollectionSourcePath = root.SourcePath;
        Id = BuildId(root.SourcePath);
        // Scope the tab to its collection so the strip filtering keeps it with the collection
        // it belongs to (same mechanism request tabs use).
        CollectionPath = root.SourcePath;
        Method = "CFG";
        Kind = RequestKind.Http;
        Hydrate(root);
    }

    private void Hydrate(CollectionRootViewModel root)
    {
        Name = root.Name + " — Settings";
        Location = root.SourcePath;

        var col = root.Collection ?? new Collection { Name = root.Name };
        var props = new NodePropertiesViewModel(NodePropertiesViewModel.Kind.Collection, col);
        props.SaveRequested += OnPropsSaved;
        Props = props;

        RequestCount = root.Collection is null ? 0 : CollectionsViewModel.CountRequestsPublic(root.Collection);
        CollectionEnvCount = root.Collection?.Environments.Count ?? 0;
        GlobalEnvCount = _collections.GlobalEnvironments.Count;

        // Land in view mode after (re)hydrate — a fresh save returns you to the rendered docs.
        IsDocsEditing = false;
    }

    private void OnPropsSaved(object? sender, NodePropertiesSaveEventArgs e)
    {
        var reloaded = _collections.ApplyCollectionSettings(CollectionSourcePath, e.Snapshot);
        // Rehydrate from the reloaded (swapped) root so a second save doesn't build on stale
        // state, and the Overview counts refresh.
        if (reloaded is not null)
        {
            if (Props is not null) Props.SaveRequested -= OnPropsSaved;
            Hydrate(reloaded);
        }
        IsDirty = false;
    }
}
